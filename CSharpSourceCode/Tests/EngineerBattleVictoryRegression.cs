// Standalone .NET Framework regression; excluded from TOR_Core.csproj.
// Arguments: mod DLL directory and Bannerlord installation root. No game files are written.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TOR_Core.Quests;

internal static class EngineerBattleVictoryRegression
{
    private static string[] _assemblyDirectories;
    private static int _advancements;
    private static int _aiUpdates;

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

    private static T Empty<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    private static void Set<T>(T target, string field, object value) { AccessTools.Field(typeof(T), field).SetValue(target, value); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var campaign = Empty<Campaign>();
        var mainParty = Empty<MobileParty>();
        var mainPartyBase = Empty<PartyBase>();
        var target = Empty<MobileParty>();
        var targetBase = Empty<PartyBase>();
        var targetMapParty = Empty<MapEventParty>();
        Set(campaign, "<MainParty>k__BackingField", mainParty);
        Set(mainParty, "<Party>k__BackingField", mainPartyBase);
        Set(targetBase, "<MobileParty>k__BackingField", target);
        Set(targetMapParty, "<Party>k__BackingField", targetBase);
        FieldInfo currentField = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        object previousCampaign = currentField.GetValue(null);
        currentField.SetValue(null, campaign);
        var harmony = new Harmony("tor.regression.engineer-victory");
        try
        {
            // Execute the real event handler and its engine predicates. Observe its
            // progression/AI boundaries without opening journals or starting party AI.
            harmony.Patch(AccessTools.Method(typeof(EngineerQuest), "UpdateProgressOnQuest"), prefix: new HarmonyMethod(typeof(EngineerBattleVictoryRegression), "RecordAdvance"));
            harmony.Patch(AccessTools.Method(typeof(EngineerQuest), "SetQuestPartyAiAfterBattle"), prefix: new HarmonyMethod(typeof(EngineerBattleVictoryRegression), "RecordAiUpdate"));
            int checks = 0;
            foreach (var playerSide in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            foreach (var questState in new[] { EngineerQuestStates.Cultisthunt, EngineerQuestStates.RogueEngineerhunt })
            {
                var playerEventSide = Empty<MapEventSide>();
                var opponentEventSide = Empty<MapEventSide>();
                var battle = Empty<MapEvent>();
                Set(mainPartyBase, "_mapEventSide", playerEventSide);
                Set(playerEventSide, "_mapEvent", battle);
                Set(playerEventSide, "<MissionSide>k__BackingField", playerSide);
                Set(opponentEventSide, "<MissionSide>k__BackingField", playerSide.GetOppositeSide());
                var opponents = new MBList<MapEventParty>();
                opponents.Add(targetMapParty);
                Set(opponentEventSide, "_battleParties", opponents);
                var sides = new MapEventSide[2];
                sides[(int)playerSide] = playerEventSide;
                sides[(int)playerSide.GetOppositeSide()] = opponentEventSide;
                Set(battle, "_sides", sides);
                Set(battle, "_mapEventType", MapEvent.BattleTypes.FieldBattle);
                BattleState victory = playerSide == BattleSideEnum.Attacker ? BattleState.AttackerVictory : BattleState.DefenderVictory;
                BattleState defeat = playerSide == BattleSideEnum.Attacker ? BattleState.DefenderVictory : BattleState.AttackerVictory;
                foreach (var outcome in new[] { defeat, BattleState.None, BattleState.DefenderPullBack, victory })
                {
                    Check(target, battle, questState, outcome, outcome == victory, playerSide + " / " + questState + " / " + outcome);
                    checks++;
                }
                opponents.Clear();
                Check(target, battle, questState, victory, false, "unrelated opponent");
                checks++;
            }
            Console.WriteLine("PASS: " + checks + " production-handler cases");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentField.SetValue(null, previousCampaign);
        }
    }

    private static void Check(MobileParty target, MapEvent battle, EngineerQuestStates state, BattleState outcome, bool expected, string label)
    {
        var quest = Empty<EngineerQuest>();
        Set(quest, "_targetParty", target);
        Set(quest, "_currentActiveLog", state);
        Set(battle, "_battleState", outcome);
        _advancements = 0;
        _aiUpdates = 0;
        AccessTools.Method(typeof(EngineerQuest), "QuestBattleEnded").Invoke(quest, new object[] { battle });
        bool skipsPrisonerDialogue = (bool)AccessTools.Field(typeof(EngineerQuest), "_skipImprisonment").GetValue(quest);
        if (_advancements != (expected ? 1 : 0) || _aiUpdates != (expected ? 1 : 0) || skipsPrisonerDialogue != expected)
            throw new Exception(label + ": expected progression " + expected + ", actual advances=" + _advancements + ", AI=" + _aiUpdates + ", prisoner skip=" + skipsPrisonerDialogue);
    }

    private static bool RecordAdvance() { _advancements++; return false; }
    private static bool RecordAiUpdate() { _aiUpdates++; return false; }
}
