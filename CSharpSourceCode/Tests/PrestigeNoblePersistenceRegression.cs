// Standalone regression runner. Compile against TOR_Core.dll and the installed
// TaleWorlds.CampaignSystem.dll, then pass those assembly directories as arguments.
// It exercises the production behavior through the real IDataStore interface.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TOR_Core.CampaignMechanics.Menagery;

internal static class PrestigeNoblePersistenceRegression
{
    private static string[] _assemblyDirectories;

    public static int Main(string[] args)
    {
        _assemblyDirectories = args;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try
        {
            Run();
            Console.WriteLine("PASS: political and infrastructure projects survive independent save/load roundtrips.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        var fileName = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in _assemblyDirectories)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var original = new PrestigeNobleTownBehavior();
        ProjectList(original, "_constructedBuildings").Add("building2");
        ProjectList(original, "_politicalPowerProjects").Add("powerProject1");
        SetFlag(original, "_knowsPlayer", true);
        SetFlag(original, "_receivedDemiGryphen", true);

        var saved = new Dictionary<string, object>();
        original.SyncData(new MemoryDataStore(saved, true));
        AssertList((List<string>)saved["_constructedBuildings"], "building2", "saved infrastructure projects");
        AssertList((List<string>)saved["_politicalPowerProjects"], "powerProject1", "saved political projects");

        var loaded = new PrestigeNobleTownBehavior();
        loaded.SyncData(new MemoryDataStore(saved, false));
        AssertList(ProjectList(loaded, "_constructedBuildings"), "building2", "loaded infrastructure projects");
        AssertList(ProjectList(loaded, "_politicalPowerProjects"), "powerProject1", "loaded political projects");
        if (!GetFlag(loaded, "_knowsPlayer") || !GetFlag(loaded, "_receivedDemiGryphen"))
            throw new InvalidOperationException("Existing boolean save values changed.");

        ProjectList(loaded, "_politicalPowerProjects").Add("powerProject3");
        if (ProjectList(loaded, "_constructedBuildings").Contains("powerProject3"))
            throw new InvalidOperationException("Political and infrastructure lists alias after loading.");
        var savedAgain = new Dictionary<string, object>();
        loaded.SyncData(new MemoryDataStore(savedAgain, true));
        var reloaded = new PrestigeNobleTownBehavior();
        reloaded.SyncData(new MemoryDataStore(savedAgain, false));
        var reloadedPolitical = ProjectList(reloaded, "_politicalPowerProjects");
        if (reloadedPolitical.Count != 2 || !reloadedPolitical.Contains("powerProject1") || !reloadedPolitical.Contains("powerProject3"))
            throw new InvalidOperationException("Political completion added after loading was not persisted.");

        // Older versions accidentally saved infrastructure ids in both slots.
        // Loading that data must preserve infrastructure history without claiming
        // to recover political completion ids that were never serialized.
        var legacyProjects = new List<string> { "building2" };
        var legacySaved = new Dictionary<string, object>
        {
            { "_constructedBuildings", legacyProjects },
            { "_politicalPowerProjects", legacyProjects },
            { "_knowsPlayer", true },
            { "_receivedDemiGryphen", false }
        };
        var legacyLoaded = new PrestigeNobleTownBehavior();
        legacyLoaded.SyncData(new MemoryDataStore(legacySaved, false));
        AssertList(ProjectList(legacyLoaded, "_constructedBuildings"), "building2", "legacy infrastructure projects");
        if (ProjectList(legacyLoaded, "_politicalPowerProjects").Contains("powerProject1"))
            throw new InvalidOperationException("Legacy political history was fabricated.");
        ProjectList(legacyLoaded, "_politicalPowerProjects").Add("powerProject1");
        var legacySavedAgain = new Dictionary<string, object>();
        legacyLoaded.SyncData(new MemoryDataStore(legacySavedAgain, true));
        var legacyReloaded = new PrestigeNobleTownBehavior();
        legacyReloaded.SyncData(new MemoryDataStore(legacySavedAgain, false));
        if (!ProjectList(legacyReloaded, "_constructedBuildings").Contains("building2") ||
            !ProjectList(legacyReloaded, "_politicalPowerProjects").Contains("powerProject1"))
            throw new InvalidOperationException("A post-fix legacy save lost project completion ids.");
    }

    private static FieldInfo Field(string name)
    {
        var field = typeof(PrestigeNobleTownBehavior).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("Missing production field: " + name);
        return field;
    }

    private static List<string> ProjectList(PrestigeNobleTownBehavior behavior, string name)
    {
        return (List<string>)Field(name).GetValue(behavior);
    }

    private static void SetFlag(PrestigeNobleTownBehavior behavior, string name, bool value)
    {
        Field(name).SetValue(behavior, value);
    }

    private static bool GetFlag(PrestigeNobleTownBehavior behavior, string name)
    {
        return (bool)Field(name).GetValue(behavior);
    }

    private static void AssertList(List<string> list, string expected, string description)
    {
        if (list.Count != 1 || list[0] != expected)
            throw new InvalidOperationException(description + ": expected [" + expected + "] but got [" + string.Join(",", list) + "].");
    }

    private sealed class MemoryDataStore : IDataStore
    {
        private readonly Dictionary<string, object> _values;
        private readonly Dictionary<object, object> _copies = new Dictionary<object, object>();
        public bool IsSaving { get; private set; }
        public bool IsLoading { get { return !IsSaving; } }

        public MemoryDataStore(Dictionary<string, object> values, bool saving)
        {
            _values = values;
            IsSaving = saving;
        }

        public bool SyncData<T>(string key, ref T value)
        {
            if (IsSaving)
            {
                _values[key] = Copy(value);
                return true;
            }
            object saved;
            if (!_values.TryGetValue(key, out saved)) return false;
            value = (T)Copy(saved);
            return true;
        }

        private object Copy(object value)
        {
            var list = value as List<string>;
            if (list == null) return value;
            object copy;
            if (!_copies.TryGetValue(list, out copy))
            {
                copy = new List<string>(list);
                _copies.Add(list, copy);
            }
            return copy;
        }
    }
}
