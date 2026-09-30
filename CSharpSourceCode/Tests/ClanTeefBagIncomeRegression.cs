// Standalone .NET Framework regression; not compiled into the mod.
// Arguments: compiled TOR_Core DLL directory, matching Bannerlord install root.
// Only unrelated native base-finance totals are supplied at a boundary. The
// actual TOR overrides, clan/hero/fief getters, stash and bag filters execute.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using TOR_Core.Extensions.ExtendedInfoSystem;
using TOR_Core.Models;

internal static class ClanTeefBagIncomeRegression
{
    private const float NativeIncome = 100f;
    private const float NativeGoldChange = 60f;
    private static string[] _assemblyDirectories;
    private static Campaign _campaign;
    private static Game _game;
    private static Hero _hero;
    private static Clan _playerClan;
    private static Clan _otherClan;
    private static int _caseNumber;
    private static int _checks;
    private static int _dailyBaseCalls;
    private static readonly List<string> Failures = new List<string>();

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
        try { Run(); return Failures.Count == 0 ? 0 : 1; }
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

    private static T Uninitialized<T>()
    {
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
    }

    private static void SetField(Type type, object target, string name, object value)
    {
        FieldInfo field = AccessTools.Field(type, name);
        if (field == null) throw new Exception("Required real game field missing: " + type.FullName + "." + name);
        field.SetValue(target, value);
    }

    private static void Identity(MBObjectBase target, string id)
    {
        SetField(typeof(MBObjectBase), target, "<StringId>k__BackingField", id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        FieldInfo campaignCurrent = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        FieldInfo gameCurrent = AccessTools.Field(typeof(Game), "_current");
        FieldInfo infoCurrent = AccessTools.Field(typeof(ExtendedInfoManager), "_instance");
        object oldCampaign = campaignCurrent.GetValue(null);
        object oldGame = gameCurrent.GetValue(null);
        object oldInfo = infoCurrent.GetValue(null);
        _campaign = Uninitialized<Campaign>();
        _game = Uninitialized<Game>();
        SetField(typeof(Game), _game, "<GameType>k__BackingField", _campaign);
        var behaviorManager = Uninitialized<CampaignBehaviorManager>();
        SetField(typeof(CampaignBehaviorManager), behaviorManager, "_campaignBehaviors", new List<CampaignBehaviorBase>());
        SetField(typeof(Campaign), _campaign, "_campaignBehaviorManager", behaviorManager);
        var info = Uninitialized<ExtendedInfoManager>();
        FieldInfo heroInfos = AccessTools.Field(typeof(ExtendedInfoManager), "_heroInfos");
        heroInfos.SetValue(info, Activator.CreateInstance(heroInfos.FieldType));
        campaignCurrent.SetValue(null, _campaign);
        gameCurrent.SetValue(null, _game);
        infoCurrent.SetValue(null, info);
        var harmony = new Harmony("tor.regression.clan-teef-bag-income");
        try
        {
            // These native methods own unrelated wages/taxes/transfers. Supplying
            // their independent totals reproduces their real private-internal
            // call contract without running a complete campaign simulation.
            // No TOR method or eligibility/roster getter is patched.
            harmony.Patch(AccessTools.Method(typeof(DefaultClanFinanceModel), "CalculateClanIncome"),
                prefix: new HarmonyMethod(typeof(ClanTeefBagIncomeRegression), "SupplyNativeIncome"));
            harmony.Patch(AccessTools.Method(typeof(DefaultClanFinanceModel), "CalculateClanGoldChange"),
                prefix: new HarmonyMethod(typeof(ClanTeefBagIncomeRegression), "SupplyNativeGoldChange"));

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_MC", 1, false);
            Check(_playerClan, 10, "one eligible bag");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_BS", 3, false);
            Check(_playerClan, 30, "three bags");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_MC", 2, false);
            AddFief(_playerClan, _playerClan, "castle_B_BX", 5, true);
            Check(_playerClan, 70, "multiple player-owned camps including a castle");

            Reset("aserai");
            Check(_playerClan, 0, "no fiefs");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_MC", 0, false);
            Check(_playerClan, 0, "camp without bags");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_MC", 0, false, 7);
            Check(_playerClan, 0, "unrelated stash items");

            Reset("empire");
            AddFief(_playerClan, _playerClan, "town_A_MC", 3, false);
            Check(_playerClan, 0, "non-Greenskin player");

            Reset("aserai");
            AddFief(_otherClan, _otherClan, "town_A_MC", 3, false);
            Check(_otherClan, 0, "nonplayer clan");

            Reset("aserai");
            // Deliberately include another owner's fief in the supplied cache
            // to isolate the existing explicit owner guard.
            AddFief(_playerClan, _otherClan, "town_A_MC", 3, false);
            Check(_playerClan, 0, "fief owned by another hero");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_EMP", 3, false);
            Check(_playerClan, 0, "ordinary settlement outside Greenskin camp suffixes");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "castle_A_BZ", 3, true);
            Check(_playerClan, 30, "eligible castle");

            Reset("aserai");
            AddFief(_playerClan, _playerClan, "town_A_MC", 2, false, 7);
            AddFief(_playerClan, _playerClan, "town_B_EMP", 3, false);
            AddFief(_playerClan, _otherClan, "castle_C_BX", 5, true);
            Check(_playerClan, 20, "only eligible bags contribute in mixed fief and stash inputs");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            campaignCurrent.SetValue(null, oldCampaign);
            gameCurrent.SetValue(null, oldGame);
            infoCurrent.SetValue(null, oldInfo);
        }
        foreach (string failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
        Console.WriteLine("RESULT: " + (_checks - Failures.Count) + "/" + _checks + " production-model checks passed.");
    }

    private static void Reset(string cultureId)
    {
        _caseNumber++;
        _dailyBaseCalls = 0;
        var culture = Uninitialized<CultureObject>();
        Identity(culture, cultureId);
        _hero = Uninitialized<Hero>();
        Identity(_hero, "teef_test_hero_" + _caseNumber);
        _hero.Culture = culture;
        var character = Uninitialized<CharacterObject>();
        Identity(character, "teef_test_character_" + _caseNumber);
        SetField(typeof(CharacterObject), character, "_heroObject", _hero);
        SetField(typeof(Hero), _hero, "_characterObject", character);
        SetField(typeof(Game), _game, "<PlayerTroop>k__BackingField", character);
        _playerClan = NewClan("teef_player_" + _caseNumber, _hero);
        SetField(typeof(Hero), _hero, "_clan", _playerClan);
        SetField(typeof(Campaign), _campaign, "<PlayerDefaultFaction>k__BackingField", _playerClan);
        var otherHero = Uninitialized<Hero>();
        otherHero.Culture = culture;
        Identity(otherHero, "teef_other_hero_" + _caseNumber);
        _otherClan = NewClan("teef_other_" + _caseNumber, otherHero);
        SetField(typeof(Hero), otherHero, "_clan", _otherClan);
    }

    private static Clan NewClan(string id, Hero leader)
    {
        var clan = Uninitialized<Clan>();
        Identity(clan, id);
        SetField(typeof(Clan), clan, "_leader", leader);
        SetField(typeof(Clan), clan, "_fiefsCache", new MBList<Town>());
        return clan;
    }

    private static void AddFief(Clan listClan, Clan ownerClan, string id, int bags, bool castle, int otherItems = 0)
    {
        var settlement = Uninitialized<Settlement>();
        Identity(settlement, id);
        SetField(typeof(Settlement), settlement, "Stash", new ItemRoster());
        var town = Uninitialized<Town>();
        SetField(typeof(Town), town, "_ownerClan", ownerClan);
        SetField(typeof(Town), town, "_isCastle", castle);
        var party = Uninitialized<PartyBase>();
        SetField(typeof(PartyBase), party, "<Settlement>k__BackingField", settlement);
        SetField(typeof(SettlementComponent), town, "_owner", party);
        settlement.Town = town;
        if (bags > 0)
        {
            var item = Uninitialized<ItemObject>();
            Identity(item, "tor_gs_teef_bag");
            settlement.Stash.AddToCounts(item, bags);
        }
        if (otherItems > 0)
        {
            var item = Uninitialized<ItemObject>();
            Identity(item, "unrelated_item");
            settlement.Stash.AddToCounts(item, otherItems);
        }
        ((MBList<Town>)AccessTools.Field(typeof(Clan), "_fiefsCache").GetValue(listClan)).Add(town);
    }

    private static void Check(Clan clan, int bagGold, string label)
    {
        var model = new TORClanFinanceModel();
        ExplainedNumber income = model.CalculateClanIncome(clan, true, false, true);
        CheckValue(label + " / income preview", income, NativeIncome + bagGold, bagGold, true);
        ExplainedNumber preview = model.CalculateClanGoldChange(clan, true, false, true);
        CheckValue(label + " / net preview", preview, NativeGoldChange + bagGold, bagGold, true);
        // The actual native DailyTickClan passes false,true,false. A repeat
        // within the existing TTL must return the already computed total once.
        ExplainedNumber daily = model.CalculateClanGoldChange(clan, false, true, false);
        ExplainedNumber cachedDaily = model.CalculateClanGoldChange(clan, false, true, false);
        CheckValue(label + " / daily payout", daily, NativeGoldChange + bagGold, bagGold, false);
        CheckValue(label + " / cached daily payout", cachedDaily, NativeGoldChange + bagGold, bagGold, false);
        _checks++;
        if (_dailyBaseCalls != 1) Failures.Add(label + " / cache: expected one base daily calculation, got " + _dailyBaseCalls);
        else Console.WriteLine("PASS: " + label + " / cache computes once");
    }

    private static void CheckValue(string label, ExplainedNumber actual, float expected, int bagGold, bool descriptions)
    {
        _checks++;
        int lines = 0;
        float displayedBagGold = 0;
        if (descriptions)
        {
            foreach (var line in actual.GetLines())
            {
                if (line.name == "Teef Bags") { lines++; displayedBagGold += line.number; }
            }
        }
        if (actual.ResultNumber != expected ||
            (descriptions && (displayedBagGold != bagGold || lines != (bagGold > 0 ? 1 : 0))))
        {
            Failures.Add(label + ": expected total=" + expected + " bag line=" + bagGold +
                "; got total=" + actual.ResultNumber + " bag line=" + displayedBagGold + " lines=" + lines);
        }
        else Console.WriteLine("PASS: " + label);
    }

    private static bool SupplyNativeIncome(bool __1, ref ExplainedNumber __result)
    {
        __result = new ExplainedNumber(NativeIncome, __1);
        return false;
    }

    private static bool SupplyNativeGoldChange(bool __1, bool __2, ref ExplainedNumber __result)
    {
        if (__2) _dailyBaseCalls++;
        __result = new ExplainedNumber(NativeGoldChange, __1);
        return false;
    }
}
