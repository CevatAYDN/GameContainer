using System.Collections.Generic;
using UnityEngine;
using System.IO;

namespace Nexus.Editor
{
    internal static class NexusLang
    {
        private static Dictionary<string, string> s_strings;
        private static readonly string[] s_supportedLocales = { "en", "tr", "ja", "zh", "ko" };
        private static string s_currentLocale = "en";

        public static string CurrentLocale => s_currentLocale;
        public static IReadOnlyList<string> SupportedLocales => s_supportedLocales;

        static NexusLang()
        {
            s_currentLocale = UnityEditor.EditorPrefs.GetString("Nexus_Locale", "en");
            LoadLocale(s_currentLocale);
        }

        public static void LoadLocale(string locale)
        {
            s_currentLocale = locale;
            s_strings = new Dictionary<string, string>();

            // Default English fallback
            AddDefaults();

            // Try to load locale-specific overrides from JSON
            string path = $"Packages/com.nexus.core/Editor/Locales/{locale}.json";
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var overrides = JsonUtility.FromJson<LocaleData>(json);
                    if (overrides?.entries != null)
                    {
                        foreach (var entry in overrides.entries)
                        {
                            if (!string.IsNullOrEmpty(entry.key))
                                s_strings[entry.key] = entry.value;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[NexusLang] Failed to load locale '{locale}': {ex.Message}");
                }
            }
        }

        public static string Get(string key)
        {
            if (s_strings != null && s_strings.TryGetValue(key, out var val))
                return val;
            return key;
        }

        private static void AddDefaults()
        {
            s_strings["dashboard"] = "Dashboard";
            s_strings["system_active"] = "SYSTEM ACTIVE";
            s_strings["system_standby"] = "SYSTEM STANDBY";
            s_strings["contexts"] = "Contexts";
            s_strings["handlers"] = "Handlers";
            s_strings["roots"] = "Roots";
            s_strings["models"] = "Models";
            s_strings["services"] = "Services";
            s_strings["commands"] = "Commands";
            s_strings["views"] = "Views";
            s_strings["build_validation"] = "BUILD VALIDATION";
            s_strings["run_validation"] = "Run Build Validation";
            s_strings["rerun_validation"] = "Re-run Validation";
            s_strings["not_run_yet"] = "Not run yet. Click below to validate.";
            s_strings["project_overview"] = "PROJECT OVERVIEW";
            s_strings["runtime_metrics"] = "RUNTIME METRICS";
            s_strings["live_models"] = "LIVE MODELS";
            s_strings["quick_actions"] = "ALL TOOLS";
            s_strings["open"] = "Open";
            s_strings["framework"] = "FRAMEWORK";
            s_strings["framework_desc"] = "Nexus Observable Architecture v0.3.0\n" +
                "Unity 6 • UI Toolkit • MIT License\n\n" +
                "Built on a 0-GC, JIT-free generic observable framework with:\n" +
                "• Causal Tracing — zero-allocation causality tracking\n" +
                "• 4 Execution Modes — Sequential, Concurrent, Exclusive, Composite\n" +
                "• Build Validation — catches priority conflicts before compile\n" +
                "• Auto-Discovery — Lifecycle, Commands, Views and Mediators\n" +
                "• Command Pooling — automatic pooling for 0-GC steady-state\n\n" +
                "Editor Suite: 9 plugins, Code Generator, Live Tracer, Graph Viewer, Type Analyzer";
            s_strings["signals"] = "Signals";
            s_strings["total_sigs"] = "Total Sigs";
            s_strings["gc_memory"] = "GC Memory";
            s_strings["pass"] = "PASS";
            s_strings["fail"] = "FAIL";
            s_strings["errors"] = "Errors";
            s_strings["warnings"] = "Warnings";
            s_strings["no_reactive_models"] = "No IReactiveModel instances found.";
            s_strings["more"] = "more...";
            s_strings["ready"] = "Ready. Enter Play Mode to activate the system.";
            s_strings["no_roots"] = "Create a Root via Context Wizard to get started.";
            s_strings["live_hint"] = "Live — {0} context(s) active.";
            s_strings["perf_signals"] = "Signals/s";
            s_strings["perf_commands"] = "Commands/s";
            s_strings["action_wizard_title"] = "Context Wizard";
            s_strings["action_wizard_desc"] = "Create Root contexts & generate code";
            s_strings["action_hierarchy_title"] = "Hierarchy & Data";
            s_strings["action_hierarchy_desc"] = "Inspect DI container & context tree (live)";
            s_strings["action_explorer_title"] = "Signal Explorer";
            s_strings["action_explorer_desc"] = "View signal/command mappings & test fire";
            s_strings["action_tracer_title"] = "Live Tracer";
            s_strings["action_tracer_desc"] = "Monitor signal chains in real-time";
            s_strings["action_graph_title"] = "Signal Graph";
            s_strings["action_graph_desc"] = "Visual graph of signal → command flow";
            s_strings["action_gamemanager_title"] = "Game Manager";
            s_strings["action_gamemanager_desc"] = "Model/signal/command overview & performance";
            s_strings["action_typeanalyzer_title"] = "Type Analyzer";
            s_strings["action_typeanalyzer_desc"] = "Analyze type coupling & [Inject] dependencies";
            s_strings["action_help_title"] = "Help & Docs";
            s_strings["action_help_desc"] = "Quick start guides, API reference, samples";
            s_strings["tab_dashboard"] = "Dashboard";
            s_strings["tab_wizard"] = "Wizard";
            s_strings["tab_hierarchy"] = "Hierarchy";
            s_strings["tab_explorer"] = "Explorer";
            s_strings["tab_tracer"] = "Tracer";
            s_strings["tab_graph"] = "Graph";
            s_strings["tab_gamemanager"] = "Game Manager";
            s_strings["tab_typeanalyzer"] = "Type Analyzer";
            s_strings["tab_help"] = "Help";

            s_strings["wizard_title"] = "CONTEXT CREATION & UTILITIES WIZARD";
            s_strings["wizard_create_manifest"] = "Create Default Bootstrap Manifest";
            s_strings["wizard_gen_skeleton"] = "Generate Skeleton from Manifest";
            s_strings["wizard_paths_config"] = "Paths Configuration";
            s_strings["wizard_create_root"] = "Create Root & ContextData";
            s_strings["wizard_gen_view"] = "Generate View & Mediator Files";
            s_strings["wizard_gen_signal"] = "Generate Signal & Command Files";
            s_strings["wizard_gen_service"] = "Generate Service Files";
            s_strings["wizard_delete_root"] = "DELETE ROOT & ALL RELATED ASSETS";
            s_strings["wizard_scan_unused"] = "Scan for Unused Signals (Regex)";
            s_strings["wizard_no_roots"] = "No active Roots found in scene. Create a Root first.";
            s_strings["wizard_no_roots_short"] = "No active Roots found in scene.";
            s_strings["wizard_assembly_scopes"] = "Assembly Scopes ({0} selected)";

            s_strings["hierarchy_title"] = "HIERARCHY GRAPH & DI DATA INSPECTOR";
            s_strings["hierarchy_di_inspector"] = "DI CONTAINER INSPECTOR";
            s_strings["hierarchy_none_resolved"] = "  None resolved.";
            s_strings["hierarchy_no_fields"] = "No fields or properties available.";
            s_strings["hierarchy_fields"] = "Fields";
            s_strings["hierarchy_properties"] = "Properties";

            s_strings["explorer_title"] = "SIGNAL EXPLORER & PLAY-MODE TESTER";
            s_strings["explorer_signal_type"] = "Signal Type";
            s_strings["explorer_handler_command"] = "Handler / Command";
            s_strings["explorer_mode"] = "Mode";
            s_strings["explorer_tester_title"] = "SIGNAL PLAY-MODE TESTER";
            s_strings["explorer_live_models_hint"] = "Live Models are only available in Play Mode.";
            s_strings["explorer_no_contexts"] = "No active Contexts found.";
            s_strings["explorer_refresh"] = "Refresh Data";
            s_strings["explorer_fire_test"] = "Fire Test Signal";
            s_strings["explorer_presets"] = "Presets";
            s_strings["explorer_select_signal"] = "Select a signal type from the list to test fire.";
            s_strings["explorer_testing_hint"] = "Signal testing is only active in Play Mode. Select a signal on the left to prepare testing.";
            s_strings["explorer_selected_signal"] = "Selected Signal: {0}";
            s_strings["explorer_no_context_target"] = "No active Contexts available. Play Mode target signal bus is missing.";
            s_strings["explorer_fired"] = "\u2713 Fired: {0} on context '{1}'";
            s_strings["explorer_fire_failed"] = "Fire failed: {0}";
            s_strings["explorer_error_context"] = "Error: Target context not selected.";
            s_strings["explorer_error_offline"] = "Error: Target context '{0}' or SignalBus is offline.";
            s_strings["explorer_error_invalid_bus"] = "Error: Invalid SignalBus implementation.";
            s_strings["explorer_create_error"] = "Create instance error: {0}";
            s_strings["explorer_unsupported_type"] = "{0}: {1} (Unsupported Type)";

            s_strings["tracer_title"] = "LIVE SIGNAL & COMMAND TRACER";
            s_strings["tracer_enable"] = "Enable Full Causal Tracing & Recompile";
            s_strings["tracer_offline"] = "Tracer is offline. Enter Play Mode to trace signals.";
            s_strings["tracer_type_filter"] = " Type:";
            s_strings["tracer_status_filter"] = " Status:";

            s_strings["graph_title"] = "SIGNAL GRAPH MAP";
            s_strings["graph_ready"] = "Graph ready. Click Refresh to build.";
            s_strings["graph_refresh"] = "Refresh";
            s_strings["graph_no_mappings"] = "No signal mappings found. Define commands or enter Play Mode.";
            s_strings["graph_overflow"] = "{0} nodes exceeds {1} limit — split into smaller contexts.";
            s_strings["graph_stats"] = "Graph: {0} signals → {1} commands ({2} nodes, {3} edges)";

            s_strings["gamemanager_title"] = "GAME MANAGER";
            s_strings["gamemanager_active"] = "\u25CF ACTIVE \u2014 Play Mode";
            s_strings["gamemanager_standby"] = "\u25CB STANDBY \u2014 Editor Mode";
            s_strings["gamemanager_open_wizard"] = "Open Context Wizard";
            s_strings["gamemanager_open_tracer"] = "Open Live Tracer";
            s_strings["gamemanager_open_graph"] = "Open Signal Graph";
            s_strings["gamemanager_refresh"] = "Refresh Now";
            s_strings["gamemanager_quick_fire"] = "QUICK FIRE \u2014 Click to dispatch signal with default values";
            s_strings["gamemanager_performance"] = "PERFORMANCE METRICS";
            s_strings["gamemanager_rate_graph"] = "RATE GRAPH";
            s_strings["gamemanager_context_label"] = "Context: {0}";
            s_strings["gamemanager_command_col"] = "Command";
            s_strings["gamemanager_signal_col"] = "Signal";
            s_strings["gamemanager_mode_col"] = "Mode";

            s_strings["typeanalyzer_title"] = "TYPE COUPLING ANALYZER";
            s_strings["typeanalyzer_analyze"] = "Analyze";
            s_strings["typeanalyzer_not_found"] = "Could not find type '{0}' in active assemblies.";
            s_strings["typeanalyzer_dependencies"] = "Dependencies (Required Injections):";
            s_strings["typeanalyzer_dependents"] = "Referenced By (Dependents):";
            s_strings["typeanalyzer_no_deps"] = "No [Inject] dependencies found.";
            s_strings["typeanalyzer_no_dependents"] = "No other types are injecting this type.";

            s_strings["help_title"] = "NEXUS HELP & DOCUMENTATION";
            s_strings["help_version"] = "com.nexus.core v0.3.0";
            s_strings["help_platform"] = "Unity 6 (6000.x) | C# 9+ | .NET Standard 2.1 | UI Toolkit";
            s_strings["help_whats_new"] = "New in v0.3.0: Auto-AOT generation, Thread-safe DI locking,\nHybrid Queue interleaving fix, Turkish locale, 0-GC encapsulation.\nRequires Unity 6000.5 or higher.";
            s_strings["help_import_sample"] = "Import Counter Sample";
            s_strings["help_quickstart"] = "QUICK START";
            s_strings["help_coreapi"] = "CORE API";
            s_strings["help_version_section"] = "VERSION";
            s_strings["help_samples"] = "SAMPLES";
            s_strings["help_samples_hint"] = "The Counter example demonstrates a complete MVCS cycle: Model → Command → Signal → Mediator → View.";
            s_strings["help_step1_title"] = "1. Create a Root";
            s_strings["help_step1_desc"] = "GameObject → Nexus → Create Root (or use the Wizard tab).\nThis creates a Root GameObject + ContextData ScriptableObject in the scene.";
            s_strings["help_step2_title"] = "2. Define a Signal & Model";
            s_strings["help_step2_desc"] = "Create a signal struct and a model interface/class pair.\nModels can implement IReactiveModel for auto-notification via ObservableProperty<T>.";
            s_strings["help_step3_title"] = "3. Write a Lifecycle";
            s_strings["help_step3_desc"] = "Create a class named {ScopeTag}Lifecycle implementing IContextLifecycle.\nNexus auto-discovers it. Use OnConfigure() to bind models and commands.";
            s_strings["help_step4_title"] = "4. Create Commands";
            s_strings["help_step4_desc"] = "Implement ICommand<TSignal> or IAsyncCommand<TSignal>.\nBind in lifecycle: builder.BindSignal<MySignal>().To<MyCommand>();";
            s_strings["help_step5_title"] = "5. Wire Views & Mediators";
            s_strings["help_step5_desc"] = "Extend View, add [Mediator(typeof(MyMediator))], create Mediator<MyView>.\nUse Subscribe<TSignal>() in OnBind() to react to signals.";
            s_strings["help_step6_title"] = "6. Fire Signals";
            s_strings["help_step6_desc"] = "SignalBus.Fire(new MySignal(data)) — from mediators, commands, or any [Inject]ed class.";
            s_strings["help_card_signalbus"] = "SignalBus";
            s_strings["help_card_signalbus_content"] = "Fire<T>(T signal) — synchronous dispatch\nFireAsync<T>(T signal) — awaitable async dispatch\nFireAsyncWithTimeout<T>(T, ms) — with timeout\nFireAsyncAndForget<T>(T, onError?) — fire-and-forget\nFireThreadSafe<T>(T) — from any thread\nFireNextFrame<T>(T) — deferred to next frame\nSubscribe<T>(Action<T>) / SubscribeAsync<T>(Func<T,CT,ValueTask>)";
            s_strings["help_card_contextbuilder"] = "ContextBuilder";
            s_strings["help_card_contextbuilder_content"] = "BindModel<T,I>() / BindReactiveModel<T,I>() — singleton models\nBindService<T,I>() — managed services with lifecycle\nBindSignal<T>().To<TCmd>() — fluent command binding\nBindCommand<T,TCmd>(mode, priority) — imperative binding\nBindAsyncCommand<T,TCmd>(mode, priority) — async binding";
            s_strings["help_card_execmodes"] = "Execution Modes";
            s_strings["help_card_execmodes_content"] = "Sequential — default, priority-ordered, one at a time\nConcurrent — parallel async execution\nExclusive — single-handler guarantee\nComposite — fan-in: waits for multiple signals";
            s_strings["help_card_attributes"] = "Attributes";
            s_strings["help_card_attributes_content"] = "[SignalHandler(typeof(T))] — auto-register command\n[CompositeSignalHandler(T1, T2)] — fan-in trigger\n[CrossContext(ScopeTag?)] — cross-context signal\n[Inject] — DI injection point\n[Mediator(typeof(T))] — view-mediator binding\n[LiveReload] — Play Mode asset sync\n[CommandTimeout(ms)] — async command timeout";
            s_strings["help_card_recovery"] = "Recovery";
            s_strings["help_card_recovery_content"] = "IRecoveryStrategy.OnCommandFailed(ctx) → RecoveryDecision\nRecoveryDecision.Skip() — skip and continue\nRecoveryDecision.Retry(max:3) — retry up to N times\nRecoveryDecision.Abort() — stop the chain\nRecoveryDecision.Fallback<T>() — run alternative command";
        }

        [System.Serializable]
        private class LocaleData
        {
            public LocaleEntry[] entries;
        }

        [System.Serializable]
        private class LocaleEntry
        {
            public string key;
            public string value;
        }
    }
}
