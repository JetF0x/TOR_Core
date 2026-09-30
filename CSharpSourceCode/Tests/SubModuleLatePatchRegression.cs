// Standalone regression runner. Pass the mod DLL directory and the installed
// Bannerlord root. It calls the production SubModule callback and intercepts
// the real Harmony category API to avoid installing game patches in this process.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TOR_Core;

internal static class SubModuleLatePatchRegression
{
    private static string[] _assemblyDirectories;
    private static int _installCalls;
    private static bool _throwOnInstall;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: SubModuleLatePatchRegression <mod DLL directory> <Bannerlord root>");
            return 2;
        }
        _assemblyDirectories = new[]
        {
            args[0], Path.Combine(args[1], "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "Native", "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "SandBox", "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "StoryMode", "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "CustomBattle", "bin", "Win64_Shipping_Client")
        };
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try
        {
            Run();
            Console.WriteLine("PASS: late patches install once per Harmony instance and failed installation can be retried.");
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
        foreach (var directory in _assemblyDirectories)
        {
            var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        const string probeId = "audit.submodule-late-patch-regression";
        var probe = new Harmony(probeId);
        var categoryMethod = typeof(Harmony).GetMethod("PatchCategory", new[] { typeof(string) });
        var prefix = typeof(SubModuleLatePatchRegression).GetMethod("CountCategoryInstall", BindingFlags.Static | BindingFlags.NonPublic);
        var harmonyProperty = typeof(SubModule).GetProperty("HarmonyInstance");
        var previousHarmony = harmonyProperty.GetValue(null);
        var guardField = typeof(SubModule).GetField("_latePatchesHarmonyInstance", BindingFlags.Static | BindingFlags.NonPublic);
        var previousGuard = guardField == null ? null : guardField.GetValue(null);
        probe.Patch(categoryMethod, prefix: new HarmonyMethod(prefix));
        try
        {
            _installCalls = 0;
            _throwOnInstall = false;
            harmonyProperty.SetValue(null, new Harmony("audit.tor-first-instance"));
            var subModule = new SubModule();
            subModule.OnGameInitializationFinished(null);
            subModule.OnGameInitializationFinished(null);
            AssertCalls(1, "repeated game initialization");

            // A module callback object does not define the installed patch lifetime.
            new SubModule().OnGameInitializationFinished(null);
            AssertCalls(1, "another SubModule object sharing the same Harmony instance");

            harmonyProperty.SetValue(null, new Harmony("audit.tor-second-instance"));
            subModule.OnGameInitializationFinished(null);
            subModule.OnGameInitializationFinished(null);
            AssertCalls(2, "replacement Harmony instance");

            harmonyProperty.SetValue(null, new Harmony("audit.tor-retry-instance"));
            _throwOnInstall = true;
            try
            {
                subModule.OnGameInitializationFinished(null);
                throw new InvalidOperationException("Installation was expected to fail.");
            }
            catch (InvalidOperationException exception)
            {
                if (exception.Message != "Intentional category installation failure") throw;
            }
            _throwOnInstall = false;
            subModule.OnGameInitializationFinished(null);
            subModule.OnGameInitializationFinished(null);
            AssertCalls(4, "failed installation retry");
        }
        finally
        {
            probe.Unpatch(categoryMethod, HarmonyPatchType.Prefix, probeId);
            harmonyProperty.SetValue(null, previousHarmony);
            if (guardField != null) guardField.SetValue(null, previousGuard);
            _throwOnInstall = false;
        }
    }

    private static bool CountCategoryInstall(string category)
    {
        if (category != "LatePatches")
            throw new InvalidOperationException("The production late-patch category changed.");
        _installCalls++;
        if (_throwOnInstall)
            throw new InvalidOperationException("Intentional category installation failure");
        return false;
    }

    private static void AssertCalls(int expected, string scenario)
    {
        if (_installCalls != expected)
            throw new InvalidOperationException(scenario + ": expected " + expected + " category calls but got " + _installCalls + ".");
    }
}
