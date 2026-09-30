// Standalone .NET Framework regression harness, excluded from the mod project.
// Reference TOR_Core, 0Harmony, TaleWorlds.CampaignSystem, Core, Library,
// ObjectSystem, Localization and the .NET Framework netstandard facade.
// Arguments: mod DLL directory, Bannerlord installation root. Writes no game files.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.ObjectSystem;
using TOR_Core.Ink;
using TOR_Core.Utilities;

internal static class InkPartyCapacityRegression
{
    private static string[] _assemblyDirectories;
    private static int _added;
    private static string _message;

    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
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
        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        var party = (MobileParty)FormatterServices.GetUninitializedObject(typeof(MobileParty));
        var partyBase = (PartyBase)FormatterServices.GetUninitializedObject(typeof(PartyBase));
        var roster = (TroopRoster)FormatterServices.GetUninitializedObject(typeof(TroopRoster));
        var troop = (CharacterObject)FormatterServices.GetUninitializedObject(typeof(CharacterObject));
        troop.StringId = "audit_troop";
        FieldInfo objectManagerField = AccessTools.Field(typeof(MBObjectManager), "<Instance>k__BackingField");
        object previousManager = objectManagerField.GetValue(null);
        var objectManager = MBObjectManager.Init();
        objectManager.RegisterType<CharacterObject>("NPCCharacter", "NPCCharacters", 1U, true);
        objectManager.RegisterObject(troop);
        AccessTools.Field(typeof(Campaign), "<MainParty>k__BackingField").SetValue(campaign, party);
        AccessTools.Field(typeof(MobileParty), "<Party>k__BackingField").SetValue(party, partyBase);
        AccessTools.Field(typeof(PartyBase), "<MemberRoster>k__BackingField").SetValue(partyBase, roster);
        AccessTools.Field(typeof(PartyBase), "_cachedPartyMemberSizeLimit").SetValue(partyBase, 100);
        AccessTools.Field(typeof(TroopRoster), "_count").SetValue(roster, 2);
        FieldInfo currentField = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        object previousCampaign = currentField.GetValue(null);
        currentField.SetValue(null, campaign);
        var harmony = new Harmony("tor.regression.ink-capacity");
        try
        {
            // Observe the effect boundary; all capacity logic and roster getters execute
            // from the actual production assemblies. Native UI and roster mutations are skipped.
            harmony.Patch(AccessTools.Method(typeof(TroopRoster), "AddToCounts"), prefix: new HarmonyMethod(typeof(InkPartyCapacityRegression), "CaptureAddition"));
            harmony.Patch(AccessTools.Method(typeof(TORCommon), "Say", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(InkPartyCapacityRegression), "CaptureMessage"));
            var story = (InkStory)FormatterServices.GetUninitializedObject(typeof(InkStory));
            MethodInfo recruit = AccessTools.Method(typeof(InkStory), "ChangePartyTroopCount");
            Check(recruit, story, roster, 100, 3, 0, "full party");
            Check(recruit, story, roster, 98, 3, 0, "reward exceeds remaining capacity");
            Check(recruit, story, roster, 97, 3, 3, "reward fits exactly");
            Check(recruit, story, roster, 40, 3, 3, "spare capacity");
            Check(recruit, story, roster, 100, -3, -3, "troop removal");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentField.SetValue(null, previousCampaign);
            objectManagerField.SetValue(null, previousManager);
        }
    }

    private static void Check(MethodInfo recruit, InkStory story, TroopRoster roster, int total, int reward, int expected, string label)
    {
        AccessTools.Field(typeof(TroopRoster), "_totalRegulars").SetValue(roster, total - 1);
        AccessTools.Field(typeof(TroopRoster), "_totalHeroes").SetValue(roster, 1);
        _added = 0;
        _message = null;
        recruit.Invoke(story, new object[] { "audit_troop", reward });
        if (_added != expected || (expected == 0 && _message != "ERROR, Party maximum size exceeded."))
            throw new Exception(label + ": expected addition " + expected + ", actual " + _added + ", message " + _message);
        Console.WriteLine("PASS: " + label);
    }

    private static bool CaptureAddition(int __1, ref int __result) { _added += __1; __result = 0; return false; }
    private static bool CaptureMessage(string __0) { _message = __0; return false; }
}
