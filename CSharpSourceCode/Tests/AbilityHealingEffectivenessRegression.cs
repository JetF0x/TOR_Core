// Standalone .NET Framework regression harness, excluded from TOR_Core.csproj.
// Reference TOR_Core, 0Harmony, TaleWorlds.CampaignSystem, Core, Library,
// MountAndBlade, CustomBattle, DotNet, ObjectSystem, Localization and the netstandard facade.
// Arguments: mod DLL directory, Bannerlord installation root. Writes no game files.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TOR_Core.AbilitySystem;
using TOR_Core.Extensions;
using TOR_Core.Models;

internal static class AbilityHealingEffectivenessRegression
{
    private static string[] _assemblyDirectories;
    private static float _effectiveness;
    private static float _perkMultiplier;
    private static int _perkCalls;

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
        var model = new TORAbilityModel();
        var game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        var customGame = (CustomGame)FormatterServices.GetUninitializedObject(typeof(CustomGame));
        var hero = (Hero)FormatterServices.GetUninitializedObject(typeof(Hero));
        var heroCharacter = (CharacterObject)FormatterServices.GetUninitializedObject(typeof(CharacterObject));
        var troopCharacter = (CharacterObject)FormatterServices.GetUninitializedObject(typeof(CharacterObject));
        var heroCaster = (Agent)FormatterServices.GetUninitializedObject(typeof(Agent));
        var troopCaster = (Agent)FormatterServices.GetUninitializedObject(typeof(Agent));
        var target = (Agent)FormatterServices.GetUninitializedObject(typeof(Agent));
        AccessTools.FieldRefAccess<Hero, CharacterObject>("_characterObject")(hero) = heroCharacter;
        AccessTools.FieldRefAccess<CharacterObject, Hero>("_heroObject")(heroCharacter) = hero;
        var agentCharacter = AccessTools.FieldRefAccess<Agent, BasicCharacterObject>("_character");
        agentCharacter(heroCaster) = heroCharacter;
        agentCharacter(troopCaster) = troopCharacter;
        FieldInfo gameType = AccessTools.Field(typeof(Game), "<GameType>k__BackingField");
        gameType.SetValue(game, campaign);
        FieldInfo currentGame = AccessTools.Field(typeof(Game), "_current");
        object previousGame = currentGame.GetValue(null);
        currentGame.SetValue(null, game);
        var harmony = new Harmony("tor.regression.ability-healing-effectiveness");
        try
        {
            if (!heroCaster.IsHero || heroCaster.GetHero() != hero || troopCaster.IsHero)
                throw new Exception("The managed caster setup did not match production hero lookup.");
            // Only modifier boundaries are controlled. CalculateAbilityHealing and
            // ApplyHealingModifiers execute from the real mod, including hero lookup.
            harmony.Patch(AccessTools.Method(typeof(TORAbilityModel), "GetSkillEffectivenessForAbilityDamage"),
                prefix: new HarmonyMethod(typeof(AbilityHealingEffectivenessRegression), "GetEffectiveness"));
            harmony.Patch(AccessTools.Method(typeof(TORAbilityModel), "GetPerkEffectsOnAbilityDamage"),
                prefix: new HarmonyMethod(typeof(AbilityHealingEffectivenessRegression), "GetPerkMultiplier"));

            var spell = new AbilityTemplate { AbilityType = AbilityType.Spell };
            int failures = 0;
            // TriggeredEffect already turns Storm of Renewal's 30 into 33 at
            // Spellcraft 200. Its zero-variance input also works on the audited base.
            failures += Check(model, "hero healing already includes skill", heroCaster, target, spell, 33, 1.1f, 1f, 33, 1);
            failures += Check(model, "preserve a 25 percent perk bonus", heroCaster, target, spell, 33, 1.1f, 1.25f, 41, 1);
            failures += Check(model, "preserve a reduced perk multiplier", heroCaster, target, spell, 50, 1.25f, .9f, 45, 1);
            failures += Check(model, "no skill bonus control", heroCaster, target, spell, 30, 1f, 1f, 30, 1);
            failures += Check(model, "non-hero caster", troopCaster, target, spell, 33, 1.1f, 1.25f, 33, 0);
            failures += Check(model, "null ability template", heroCaster, target, null, 33, 1.1f, 1.25f, 33, 0);
            failures += Check(model, "null target", heroCaster, null, spell, 33, 1.1f, 1.25f, 33, 0);
            failures += Check(model, "null caster", null, target, spell, 33, 1.1f, 1.25f, 33, 0);
            failures += Check(model, "zero base healing", heroCaster, target, spell, 0, 1.1f, 1.25f, 0, 0);
            failures += Check(model, "negative base healing", heroCaster, target, spell, -1, 1.1f, 1.25f, -1, 0);
            gameType.SetValue(game, customGame);
            failures += Check(model, "non-campaign game", heroCaster, target, spell, 33, 1.1f, 1.25f, 33, 0);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentGame.SetValue(null, previousGame);
        }
    }

    private static int Check(TORAbilityModel model, string label, Agent caster, Agent target, AbilityTemplate spell,
        int baseHealing, float effectiveness, float perkMultiplier, int expectedHealing, int expectedPerkCalls)
    {
        _effectiveness = effectiveness;
        _perkMultiplier = perkMultiplier;
        _perkCalls = 0;
        int actual = model.CalculateAbilityHealing(caster, target, baseHealing, spell);
        bool passed = actual == expectedHealing && _perkCalls == expectedPerkCalls;
        Console.WriteLine((passed ? "PASS: " : "FAIL: ") + label +
            "; expected " + expectedHealing + "; actual " + actual + "; perk calls " + _perkCalls);
        return passed ? 0 : 1;
    }

    private static bool GetEffectiveness(ref float __result) { __result = _effectiveness; return false; }
    private static bool GetPerkMultiplier(ref float __result) { _perkCalls++; __result = _perkMultiplier; return false; }
}
