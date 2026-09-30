// Standalone .NET Framework regression harness; intentionally excluded from TOR_Core.csproj.
// Compile against TOR_Core.dll, TaleWorlds.CampaignSystem.dll, TaleWorlds.Core.dll,
// TaleWorlds.Library.dll and TaleWorlds.ObjectSystem.dll. Run with the mod DLL directory
// and Bannerlord installation root as arguments. No game files are written.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TOR_Core.Quests;

internal static class EngineerQuestLoadRegression
{
    private static string[] _assemblyDirectories;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: EngineerQuestLoadRegression <mod DLL directory> <Bannerlord root>");
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
        try { Run(); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        foreach (string directory in _assemblyDirectories)
        {
            string path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        // Reconstruct only managed state used by the real load-repair callback.
        // Constructors would start a campaign; this harness does not simulate the engine.
        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        var manager = (QuestManager)FormatterServices.GetUninitializedObject(typeof(QuestManager));
        var quest = (EngineerQuest)FormatterServices.GetUninitializedObject(typeof(EngineerQuest));
        var quests = new MBList<QuestBase>();
        quests.Add(quest);
        SetField(typeof(QuestManager), manager, "_quests", quests);
        SetField(typeof(Campaign), campaign, "<QuestManager>k__BackingField", manager);
        FieldInfo current = typeof(Campaign).GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        object previousCampaign = current.GetValue(null);
        current.SetValue(null, campaign);
        try
        {
            MethodInfo repair = typeof(EngineerQuest).GetMethod("CorrectHeroOccupationAndClanStatus", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (EngineerQuestStates state in new[] { EngineerQuestStates.HandInCultisthunt, EngineerQuestStates.HandInRogueEngineerHunt })
            {
                SetField(typeof(EngineerQuest), quest, "_currentActiveLog", state);
                SetField(typeof(EngineerQuest), quest, "_targetParty", null);
                repair.Invoke(quest, new object[] { null });
                if (quest.TargetParty != null || quest.CurrentActiveLog != state)
                    throw new Exception("Load repair changed the completed hunt's state.");
                Console.WriteLine("PASS: " + state + " with no remaining target party");
            }
        }
        finally { current.SetValue(null, previousCampaign); }
    }

    private static void SetField(Type type, object instance, string name, object value)
    {
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
}
