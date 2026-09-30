// Standalone .NET Framework regression runner. Pass the mod DLL directory and
// Bannerlord root. Real quest RegisterEvents/OnFinalize callbacks and the real
// TOR event publisher are exercised; no replacement game classes are defined.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TOR_Core.Quests;
using TOR_Core.Quests.Careers;
using TOR_Core.Utilities;

internal static class CareerQuestEventLifecycleRegression
{
    private static string[] _assemblyDirectories;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CareerQuestEventLifecycleRegression <mod DLL directory> <Bannerlord root>");
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
        catch (Exception exception) { Console.Error.WriteLine("FAIL: " + exception); return 1; }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly; }
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
        var currentField = typeof(Campaign).GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var previousCampaign = currentField.GetValue(null);
        var previousPublisher = TORCampaignEvents.Instance;
        var campaign = (Campaign)FormatterServices.GetUninitializedObject(typeof(Campaign));
        SetField(typeof(Campaign), campaign, "<CampaignEvents>k__BackingField", new CampaignEvents());
        currentField.SetValue(null, campaign);
        try
        {
            var eventsByType = new Dictionary<Type, string[]>
            {
                { typeof(RunesmithQuest), new[] { "EnchantmentLearned" } },
                { typeof(RunelordQuest), new[] { "EnchantmentLearned", "AbilityLearned", "OathLevelChanged" } },
                { typeof(OrcBossQuest1), new[] { "BrawlWon", "TournamentWon", "TeefTransferred", "PracticeFightWon" } },
                { typeof(OrcBossQuest2), new[] { "BrawlWon", "TournamentWon", "TeefTransferred", "LordDuelWon", "PracticeFightWon" } },
                { typeof(OrcShamanQuest1), new[] { "ShrinePrayer", "TeefTransferred", "EnchantmentLearned", "ShrineLooted" } },
                { typeof(OrcShamanQuest2), new[] { "TeefTransferred", "EnchantmentLearned", "ShrineLooted" } }
            };
            int failures = 0;
            foreach (var entry in eventsByType)
            {
                try
                {
                    VerifySubscriptions(entry.Key, entry.Value);
                    Console.WriteLine("PASS: " + entry.Key.Name + " releases its original publisher and can finalize twice");
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL: " + entry.Key.Name + ": " + exception.Message);
                }
            }
            try
            {
                VerifyRuneProgressStopsAtFinalization();
                Console.WriteLine("PASS: active rune event progresses the task; finalized quest ignores later events");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL: finalized rune progress: " + exception.Message);
            }
            if (failures != 0) throw new InvalidOperationException(failures + " lifecycle scenarios failed.");
        }
        finally
        {
            currentField.SetValue(null, previousCampaign);
            TORCampaignEvents.Instance = previousPublisher;
        }
    }

    private static object CreateQuest(Type type)
    {
        // Constructors create gameplay tasks and require campaign assets. Rebuild
        // only the managed state needed by the production callbacks under test.
        var quest = FormatterServices.GetUninitializedObject(type);
        // Baseline types have destructors that dereference the current publisher.
        // They are unrelated to the engine OnFinalize contract tested here.
        GC.SuppressFinalize(quest);
        return quest;
    }

    private static void VerifySubscriptions(Type type, string[] events)
    {
        var publisher = new TORCampaignEvents();
        var quest = CreateQuest(type);
        InvokeHook(type, quest, "RegisterEvents");
        foreach (var eventName in events) AssertSubscriberCount(publisher, eventName, quest, 1);

        // Changing the global publisher must not redirect old quest teardown.
        var replacementPublisher = new TORCampaignEvents();
        InvokeHook(type, quest, "OnFinalize");
        foreach (var eventName in events)
        {
            AssertSubscriberCount(publisher, eventName, quest, 0);
            AssertSubscriberCount(replacementPublisher, eventName, quest, 0);
        }
        InvokeHook(type, quest, "OnFinalize");

        // Re-registering must not retain a second custom delegate on the old
        // publisher or leave duplicate custom delegates on the new publisher.
        InvokeHook(type, quest, "RegisterEvents");
        InvokeHook(type, quest, "RegisterEvents");
        foreach (var eventName in events) AssertSubscriberCount(replacementPublisher, eventName, quest, 1);
        InvokeHook(type, quest, "OnFinalize");
        foreach (var eventName in events) AssertSubscriberCount(replacementPublisher, eventName, quest, 0);
    }

    private static void VerifyRuneProgressStopsAtFinalization()
    {
        var publisher = new TORCampaignEvents();
        var quest = CreateQuest(typeof(RunesmithQuest));
        var task = new JournalLog(default(CampaignTime), new TextObject("Test"), new TextObject("Task"), 0, 100, LogType.Discreate);
        var journal = new MBList<JournalLog>();
        journal.Add(task);
        SetField(typeof(RunesmithQuest), quest, "_task2", task);
        SetField(typeof(QuestBase), quest, "_journalEntries", journal);
        InvokeHook(typeof(RunesmithQuest), quest, "RegisterEvents");
        publisher.OnEnchantmentLearned(null, "test_rune");
        if (task.CurrentProgress != 1) throw new InvalidOperationException("Active quest did not receive its rune event.");
        InvokeHook(typeof(RunesmithQuest), quest, "OnFinalize");
        publisher.OnEnchantmentLearned(null, "test_rune_after_finalization");
        if (task.CurrentProgress != 1) throw new InvalidOperationException("Finalized quest still updated its journal to " + task.CurrentProgress + ".");
    }

    private static void AssertSubscriberCount(TORCampaignEvents publisher, string eventName, object quest, int expected)
    {
        var field = typeof(TORCampaignEvents).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        var handlers = (Delegate)field.GetValue(publisher);
        int actual = handlers == null ? 0 : handlers.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, quest));
        if (actual != expected)
            throw new InvalidOperationException(eventName + ": expected " + expected + " quest subscriptions but got " + actual + ".");
    }

    private static void InvokeHook(Type type, object quest, string name)
    {
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(quest, null);
    }

    private static void SetField(Type type, object instance, string name, object value)
    {
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
}
