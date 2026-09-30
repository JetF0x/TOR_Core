// Standalone .NET Framework regression harness, excluded from TOR_Core.csproj.
// Reference TOR_Core, 0Harmony, TaleWorlds.CampaignSystem, Core, Library,
// MountAndBlade, Engine, ObjectSystem, Localization and the netstandard facade.
// Arguments: mod DLL directory, Bannerlord installation root. Writes no game files.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TOR_Core.AbilitySystem;
using TOR_Core.BattleMechanics.TriggeredEffect;
using TOR_Core.Extensions;
using TOR_Core.Models;
using TOR_Core.Utilities;

internal static class TriggeredEffectBoundsRegression
{
    private static string[] _assemblyDirectories;
    private static TORAbilityModel _model;
    private static float _effectiveness;
    private static int _min;
    private static int _max;
    private static int _calls;
    private static bool _healing;

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
        var harmony = new Harmony("tor.regression.triggered-effect-bounds");
        _model = new TORAbilityModel();
        // Create real engine types with only the managed state required by Trigger.
        // Native agent, mission, particles and downstream hit application are skipped.
        var game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        var caster = (Agent)FormatterServices.GetUninitializedObject(typeof(Agent));
        var character = (CharacterObject)FormatterServices.GetUninitializedObject(typeof(CharacterObject));
        AccessTools.Field(typeof(Game), "<GameType>k__BackingField").SetValue(game, campaign);
        var casterCharacter = AccessTools.FieldRefAccess<Agent, BasicCharacterObject>("_character");
        casterCharacter(caster) = character;
        FieldInfo currentGame = AccessTools.Field(typeof(Game), "_current");
        FieldInfo currentCampaign = AccessTools.Field(typeof(Campaign), "<Current>k__BackingField");
        object previousGame = currentGame.GetValue(null);
        object previousCampaign = currentCampaign.GetValue(null);
        currentGame.SetValue(null, game);
        currentCampaign.SetValue(null, campaign);
        try
        {
            Patch(harmony, AccessTools.Method(typeof(Agent), "IsActive"), "AgentIsActive");
            Patch(harmony, AccessTools.Method(typeof(GameModelsExtensions), "GetAbilityModel"), "GetModel");
            Patch(harmony, AccessTools.Method(typeof(TORAbilityModel), "GetSkillEffectivenessForAbilityDamage"), "GetEffectiveness");
            Patch(harmony, AccessTools.Method(typeof(TORAbilityModel), "CalculateStatusEffectDurationForAbility"), "KeepDuration");
            Patch(harmony, AccessTools.Method(typeof(TORAbilityModel), "CalculateRadiusForAbility"), "KeepRadius");
            Patch(harmony, AccessTools.Method(typeof(TORMissionHelper), "DamageAgents"), "CaptureDamage");
            Patch(harmony, AccessTools.Method(typeof(TORMissionHelper), "HealAgents"), "CaptureHealing");

            int failures = 0;
            // Preserve existing float-to-int truncation: 60 * 1.05f is just below 63.
            failures += Check(caster, "Fireball, no skill bonus", 60, .05f, 1f, 57, 62);
            failures += Check(caster, "Fireball, Spellcraft 250", 60, .05f, 1.125f, 64, 70);
            failures += Check(caster, "damage with larger variance", 40, .2f, 1.5f, 48, 72);
            failures += Check(caster, "damage with zero variance", 60, 0f, 1.25f, 75, 75);
            failures += Check(caster, "damage with zero effectiveness", 60, .05f, 0f, 0, 0);
            failures += Check(caster, "fractional endpoints truncate", 20, .5f, .75f, 7, 22);
            failures += Check(caster, "Storm of Renewal, Spellcraft 200", -30, 0f, 1.1f, 33, 33);
            failures += Check(caster, "healing with variance", -20, .1f, 1.5f, 27, 33);
            failures += Check(caster, "healing with no skill bonus", -20, .1f, 1f, 18, 22);
            failures += Check(caster, "healing with zero effectiveness", -30, 0f, 0f, 0, 0);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            currentGame.SetValue(null, previousGame);
            currentCampaign.SetValue(null, previousCampaign);
        }
    }

    private static void Patch(Harmony harmony, MethodBase method, string prefix)
    {
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(TriggeredEffectBoundsRegression), prefix));
    }

    private static int Check(Agent caster, string label, int amount, float variance, float effectiveness, int expectedMin, int expectedMax)
    {
        _effectiveness = effectiveness;
        _calls = 0;
        _min = _max = -1;
        _healing = false;
        var effect = new TriggeredEffect(new TriggeredEffectTemplate
        {
            StringID = "bounds-regression",
            DamageAmount = amount,
            DamageVariance = variance
        });
        effect.Trigger(new Vec3(1f, 1f, 1f), Vec3.Up, caster,
            new AbilityTemplate { AbilityType = AbilityType.Spell }, new MBList<Agent>());
        bool passed = _calls == 1 && _healing == (amount < 0) && _min == expectedMin && _max == expectedMax;
        Console.WriteLine((passed ? "PASS: " : "FAIL: ") + label +
            "; expected " + expectedMin + ".." + expectedMax + "; actual " + _min + ".." + _max +
            "; helper calls " + _calls);
        return passed ? 0 : 1;
    }

    private static bool AgentIsActive(ref bool __result) { __result = true; return false; }
    private static bool GetModel(ref TORAbilityModel __result) { __result = _model; return false; }
    private static bool GetEffectiveness(ref float __result) { __result = _effectiveness; return false; }
    private static bool KeepDuration(float __2, ref float __result) { __result = __2; return false; }
    private static bool KeepRadius(float __2, ref float __result) { __result = __2; return false; }
    private static bool CaptureDamage(int __1, int __2) { _calls++; _min = __1; _max = __2; return false; }
    private static bool CaptureHealing(int __1, int __2) { _calls++; _min = __1; _max = __2; _healing = true; return false; }
}
