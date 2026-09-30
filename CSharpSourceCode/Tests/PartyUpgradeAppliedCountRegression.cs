// Standalone .NET Framework harness; not compiled into the mod.
// Arguments: compiled TOR_Core DLL directory, matching Bannerlord install root.
// Executes production upgrade and roster code; captures gold and skill effects.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TOR_Core.CampaignMechanics;

internal static class PartyUpgradeAppliedCountRegression
{
    private const int RequestedCount = 10;
    private const int UnitGoldCost = 7;
    private const int UnitXpCost = 100;
    private const int InitialXp = RequestedCount * UnitXpCost;
    private static string[] _assemblyDirectories;
    private static int _totalWage;
    private static int _paid;
    private static int _credited;
    private static Hero _payer;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        FieldInfo currentField = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        object previousCampaign = currentField.GetValue(null);
        var campaign = Uninitialized<Campaign>();
        var models = Uninitialized<GameModels>();
        var wageModel = Uninitialized<DefaultPartyWageModel>();
        AccessTools.Field(typeof(GameModels), "<PartyWageModel>k__BackingField").SetValue(models, wageModel);
        AccessTools.Field(typeof(GameModels), "<CharacterStatsModel>k__BackingField").SetValue(models, new DefaultCharacterStatsModel());
        AccessTools.Field(typeof(Campaign), "_gameModels").SetValue(campaign, models);
        currentField.SetValue(null, campaign);
        var harmony = new Harmony("tor.regression.party-upgrade-applied-count");
        try
        {
            // Supply a deterministic current wage. The real payment-limit getters,
            // wage-cap calculation, per-character wages, XP and roster moves execute.
            harmony.Patch(AccessTools.Method(typeof(DefaultPartyWageModel), "GetTotalWage"), prefix: new HarmonyMethod(typeof(PartyUpgradeAppliedCountRegression), "SupplyTotalWage"));
            harmony.Patch(AccessTools.Method(typeof(GiveGoldAction), "ApplyBetweenCharacters"), prefix: new HarmonyMethod(typeof(PartyUpgradeAppliedCountRegression), "CapturePayment"));
            harmony.Patch(AccessTools.Method(typeof(SkillLevelingManager), "OnUpgradeTroops"), prefix: new HarmonyMethod(typeof(PartyUpgradeAppliedCountRegression), "CaptureCredit"));
            foreach (bool ownerPays in new[] { true, false })
            {
                Check(wageModel, ownerPays, 96, 100, 2, "wage cap reduces ten upgrades to two");
                Check(wageModel, ownerPays, 80, 100, 10, "all ten exactly fit wage cap");
                Check(wageModel, ownerPays, 40, 100, 10, "spare wage capacity");
                Check(wageModel, ownerPays, 99, 100, 0, "remaining wage capacity cannot fund one upgrade");
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentField.SetValue(null, previousCampaign);
        }
        foreach (string failure in Failures) Console.Error.WriteLine("FAIL: " + failure);
        if (Failures.Count != 0) Console.Error.WriteLine(Failures.Count + " of 8 production-upgrade checks failed.");
    }

    private static void Check(DefaultPartyWageModel wageModel, bool ownerPays, int totalWage, int limit, int applied, string label)
    {
        var source = Uninitialized<CharacterObject>();
        var target = Uninitialized<CharacterObject>();
        AccessTools.Field(typeof(BasicCharacterObject), "<Level>k__BackingField").SetValue(source, 15);
        AccessTools.Field(typeof(BasicCharacterObject), "<Level>k__BackingField").SetValue(target, 20);
        // Default stats/wage models give tier2 -> tier3 and wages3 -> 5.
        if (wageModel.GetCharacterWage(target) - wageModel.GetCharacterWage(source) != 2)
            throw new Exception("Expected actual default-model wage difference of two.");
        var hero = Uninitialized<Hero>();
        var party = Uninitialized<PartyBase>();
        var mobileParty = Uninitialized<MobileParty>();
        var component = Uninitialized<LordPartyComponent>();
        AccessTools.Field(typeof(PartyBase), "<MobileParty>k__BackingField").SetValue(party, mobileParty);
        AccessTools.Field(typeof(MobileParty), "<Party>k__BackingField").SetValue(mobileParty, party);
        AccessTools.Field(typeof(MobileParty), "_partyComponent").SetValue(mobileParty, component);
        AccessTools.Field(typeof(LordPartyComponent), "_leader").SetValue(component, hero);
        AccessTools.Field(typeof(LordPartyComponent), "<Owner>k__BackingField").SetValue(component, ownerPays ? hero : null);
        AccessTools.Field(typeof(LordPartyComponent), "_wagePaymentLimit").SetValue(component, limit);
        var roster = TroopRoster.CreateDummyTroopRoster();
        roster.AddToCounts(source, RequestedCount);
        roster.SetElementXp(0, InitialXp);
        AccessTools.Field(typeof(PartyBase), "<MemberRoster>k__BackingField").SetValue(party, roster);
        _totalWage = totalWage;
        _paid = 0;
        _credited = 0;
        _payer = null;
        Type argsType = typeof(TORPartyUpgraderCampaignBehavior).GetNestedType("TORTroopUpgradeArgs", BindingFlags.NonPublic);
        object upgradeArgs = Activator.CreateInstance(argsType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { source, target, RequestedCount, UnitGoldCost, UnitXpCost, 1f }, null);
        MethodInfo upgrade = AccessTools.Method(typeof(TORPartyUpgraderCampaignBehavior), "UpgradeTroop");
        upgrade.Invoke(new TORPartyUpgraderCampaignBehavior(), new object[] { wageModel, party, 0, upgradeArgs });

        string context = (ownerPays ? "owner" : "leader") + " / " + label;
        int sourceCount = roster.GetTroopCount(source);
        int targetCount = roster.GetTroopCount(target);
        int remainingXp = roster.GetElementXp(source);
        if (sourceCount != RequestedCount - applied || targetCount != applied || remainingXp != InitialXp - UnitXpCost * applied ||
            _paid != UnitGoldCost * applied || _credited != applied || (applied > 0 && _payer != hero))
        {
            Failures.Add(context + ": expected upgraded/credited=" + applied + " paid=" + UnitGoldCost * applied +
                "; got source=" + sourceCount + " target=" + targetCount + " XP=" + remainingXp + " credited=" + _credited + " paid=" + _paid);
        }
        else Console.WriteLine("PASS: " + context);
    }

    private static bool SupplyTotalWage(ref ExplainedNumber __result)
    {
        __result = new ExplainedNumber(_totalWage);
        return false;
    }

    private static bool CapturePayment(Hero __0, Hero __1, int __2)
    {
        if (__1 != null) throw new Exception("Upgrade gold should not be paid to another hero.");
        _payer = __0;
        _paid += __2;
        return false;
    }

    private static bool CaptureCredit(int __3)
    {
        _credited += __3;
        return false;
    }
}