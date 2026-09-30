// Standalone .NET Framework regression harness, excluded from TOR_Core.csproj.
// Arguments: mod DLL directory, Bannerlord installation root. Writes no game files.
// The real native base model supplies eligibility and original weights. Only the
// random draw is controlled when invoking the real native recipient selector.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TOR_Core.Models;

internal static class PlayerRescuedTroopWeightsRegression
{
    private static string[] _assemblyDirectories;
    private static float _randomDraw;

    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        _assemblyDirectories = new[]
        {
            args[0], Path.Combine(args[1], "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "Native", "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "SandBox", "bin", "Win64_Shipping_Client"),
            Path.Combine(args[1], "Modules", "StoryMode", "bin", "Win64_Shipping_Client")
        };
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try { return Run(); }
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
    private static int Run()
    {
        var game = Uninitialized<Game>();
        var campaign = Uninitialized<Campaign>();
        var regular = Uninitialized<CharacterObject>();
        SetField(regular, "_occupation", Occupation.Soldier);
        var prisoner = new TroopRosterElement(regular);
        var player = CreateWinner(regular, 2, true);
        var ai = CreateWinner(regular, 6, true);
        SetField(campaign, "<MainParty>k__BackingField", player.Party.MobileParty);
        SetField(game, "<GameType>k__BackingField", campaign);
        FieldInfo currentGame = AccessTools.Field(typeof(Game), "_current");
        object previousGame = currentGame.GetValue(null);
        currentGame.SetValue(null, game);
        FieldInfo currentCampaign = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        object previousCampaign = currentCampaign.GetValue(null);
        currentCampaign.SetValue(null, campaign);
        var harmony = new Harmony("tor.regression.player-rescued-troop-weights");
        try
        {
            if (!ReferenceEquals(PartyBase.MainParty, player.Party) ||
                player.Party.MemberRoster.Count != 1 || regular.IsHero)
                throw new Exception("The managed main-party fixture did not match production getters.");

            var nativeModel = new DefaultBattleRewardModel();
            var model = new TORBattleRewardModel();
            int failures = 0;
            failures += Check(model, nativeModel, "eligible solo player", Winners(player), prisoner, 1, true);
            failures += Check(model, nativeModel, "AI rescue remains suppressed", Winners(ai), prisoner, 1, false);
            failures += Check(model, nativeModel, "mixed winners retain native player share", Winners(player, ai), prisoner, 2, true);
            failures += Check(model, nativeModel, "player is identified by party, not winner order", Winners(ai, player), prisoner, 2, true);

            SetField(player, "_contributionToBattle", 0);
            failures += Check(model, nativeModel, "zero-contribution player remains ineligible", Winners(player, ai), prisoner, 1, false);
            SetField(player, "_contributionToBattle", -1);
            failures += Check(model, nativeModel, "negative-contribution player remains ineligible", Winners(player, ai), prisoner, 1, false);
            SetField(player, "_contributionToBattle", 2);
            TroopRoster members = player.Party.MemberRoster;
            SetField(player.Party, "<MemberRoster>k__BackingField", TroopRoster.CreateDummyTroopRoster());
            failures += Check(model, nativeModel, "empty player party remains ineligible", Winners(player, ai), prisoner, 1, false);
            SetField(player.Party, "<MemberRoster>k__BackingField", members);
            SetField(player.Party.MobileParty, "<IsVillager>k__BackingField", true);
            failures += Check(model, nativeModel, "native party-type eligibility remains unchanged", Winners(player, ai), prisoner, 1, false);
            SetField(player.Party.MobileParty, "<IsVillager>k__BackingField", false);

            var captiveHero = CreateHeroPrisoner(Hero.CharacterStates.Prisoner);
            failures += Check(model, nativeModel, "captive hero retains native player weight", Winners(player), captiveHero, 1, true);
            var releasedHero = CreateHeroPrisoner(Hero.CharacterStates.Released);
            failures += Check(model, nativeModel, "released hero retains native exclusion", Winners(player, ai), releasedHero, 0, false);
            failures += Check(model, nativeModel, "empty winner list", Winners(), prisoner, 0, false);

            // Execute the native selector, without simulating MapEvent cleanup or
            // player UI. Positive draws avoid its existing zero-draw boundary case.
            harmony.Patch(AccessTools.PropertyGetter(typeof(MBRandom), "RandomFloat"),
                prefix: new HarmonyMethod(typeof(PlayerRescuedTroopWeightsRegression), "GetRandomDraw"));
            failures += CheckRecipient(model, "solo player is a native rescue recipient", Winners(player), prisoner, .5f, player);
            failures += CheckRecipient(model, "native player share can receive a rescued troop", Winners(player, ai), prisoner, .125f, player);
            failures += CheckRecipient(model, "suppressed AI share is not reassigned to player", Winners(player, ai), prisoner, .5f, null);
            failures += CheckRecipient(model, "AI-only positive draw has no recipient", Winners(ai), prisoner, .5f, null);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentCampaign.SetValue(null, previousCampaign);
            currentGame.SetValue(null, previousGame);
        }
    }

    private static T Uninitialized<T>() where T : class
    {
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = AccessTools.Field(target.GetType(), name);
        if (field == null) throw new Exception("Missing native fixture field: " + name);
        field.SetValue(target, value);
    }

    private static MapEventParty CreateWinner(CharacterObject member, int contribution, bool hasMembers)
    {
        var party = Uninitialized<PartyBase>();
        var mobileParty = Uninitialized<MobileParty>();
        var mapEventParty = Uninitialized<MapEventParty>();
        TroopRoster members = TroopRoster.CreateDummyTroopRoster();
        if (hasMembers) members.AddToCounts(member, 1);
        SetField(party, "<MobileParty>k__BackingField", mobileParty);
        SetField(party, "<MemberRoster>k__BackingField", members);
        SetField(mobileParty, "<Party>k__BackingField", party);
        SetField(mapEventParty, "<Party>k__BackingField", party);
        SetField(mapEventParty, "_contributionToBattle", contribution);
        return mapEventParty;
    }

    private static TroopRosterElement CreateHeroPrisoner(Hero.CharacterStates state)
    {
        var hero = Uninitialized<Hero>();
        var character = Uninitialized<CharacterObject>();
        SetField(hero, "_characterObject", character);
        SetField(hero, "_heroState", state);
        SetField(hero, "<Occupation>k__BackingField", Occupation.Lord);
        SetField(character, "_heroObject", hero);
        if (!character.IsHero) throw new Exception("The managed hero fixture did not match production getters.");
        return new TroopRosterElement(character);
    }

    private static MBReadOnlyList<MapEventParty> Winners(params MapEventParty[] parties)
    {
        var winners = new MBReadOnlyList<MapEventParty>();
        foreach (MapEventParty party in parties) winners.Add(party);
        return winners;
    }

    private static int Check(TORBattleRewardModel model, DefaultBattleRewardModel nativeModel, string label,
        MBReadOnlyList<MapEventParty> winners, TroopRosterElement prisoner, int expectedNativeCount, bool expectNativePlayer)
    {
        var native = nativeModel.GetLootPrisonerChances(winners, prisoner);
        var actual = model.GetLootPrisonerChances(winners, prisoner);
        bool nativePlayer = false;
        foreach (var entry in native)
        {
            if (ReferenceEquals(entry.Key.Party, PartyBase.MainParty))
            {
                nativePlayer = true;
                if (entry.Value <= 0f) throw new Exception("Native player fixture has no positive weight.");
            }
        }
        if (native.Count != expectedNativeCount || nativePlayer != expectNativePlayer)
            throw new Exception("Unexpected native eligibility for fixture: " + label);

        bool passed = actual.Count == native.Count;
        for (int index = 0; index < Math.Min(actual.Count, native.Count); index++)
        {
            var entry = native[index];
            float expected = ReferenceEquals(entry.Key.Party, PartyBase.MainParty) ? entry.Value : 0f;
            passed &= ReferenceEquals(actual[index].Key, entry.Key) && actual[index].Value == expected;
        }
        Console.WriteLine((passed ? "PASS: " : "FAIL: ") + label +
            "; native [" + Describe(native) + "]; actual [" + Describe(actual) + "]");
        return passed ? 0 : 1;
    }

    private static string Describe(MBReadOnlyList<KeyValuePair<MapEventParty, float>> chances)
    {
        var values = new List<string>();
        foreach (var entry in chances)
            values.Add((ReferenceEquals(entry.Key.Party, PartyBase.MainParty) ? "player=" : "AI=") +
                entry.Value.ToString("G9", CultureInfo.InvariantCulture));
        return string.Join(", ", values);
    }

    private static int CheckRecipient(TORBattleRewardModel model, string label, MBReadOnlyList<MapEventParty> winners,
        TroopRosterElement prisoner, float draw, MapEventParty expected)
    {
        _randomDraw = draw;
        var chances = model.GetLootPrisonerChances(winners, prisoner);
        MethodInfo selector = AccessTools.Method(typeof(MapEvent), "FindWinnerPartyToGetCurrentLootObjectBasedOnChances");
        if (selector == null || !selector.IsStatic) throw new Exception("Native recipient selector was not found.");
        var actual = (MapEventParty)selector.Invoke(null, new object[] { chances });
        bool passed = ReferenceEquals(actual, expected);
        Console.WriteLine((passed ? "PASS: " : "FAIL: ") + label + "; draw " + draw +
            "; expected " + (expected == null ? "none" : "player") +
            "; actual " + (actual == null ? "none" : ReferenceEquals(actual.Party, PartyBase.MainParty) ? "player" : "AI"));
        return passed ? 0 : 1;
    }

    private static bool GetRandomDraw(ref float __result) { __result = _randomDraw; return false; }
}
