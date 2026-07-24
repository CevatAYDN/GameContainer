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
            s_strings["framework_desc"] = "Nexus Observable Architecture v0.4.0\n" +
                "Unity 6 • UI Toolkit • MIT License\n\n" +
                "Built on a 0-GC, JIT-free generic observable framework with:\n" +
                "• Causal Tracing — zero-allocation causality tracking\n" +
                "• 4 Execution Modes — Sequential, Concurrent, Exclusive, Composite\n" +
                "• Build Validation — catches priority conflicts before compile\n" +
                "• Auto-Discovery — Lifecycle, Commands, Views and Mediators\n" +
                "• Command Pooling — automatic pooling for 0-GC steady-state\n\n" +
                "Editor Suite: 15 plugins, Code Generator, Live Tracer, Graph Viewer, Type Analyzer";
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
            s_strings["action_casual_services_title"] = "Casual Debugger";
            s_strings["action_casual_services_desc"] = "Play-mode live control for Economy, Level, UI, Audio & Haptics";
            s_strings["tab_dashboard"] = "Dashboard";
            s_strings["tab_wizard"] = "Wizard";
            s_strings["tab_hierarchy"] = "Hierarchy";
            s_strings["tab_explorer"] = "Explorer";
            s_strings["tab_tracer"] = "Tracer";
            s_strings["tab_graph"] = "Graph";
            s_strings["tab_gamemanager"] = "Game Manager";
            s_strings["tab_typeanalyzer"] = "Type Analyzer";
            s_strings["tab_casual_services"] = "Casual Debugger";
            s_strings["tab_help"] = "Help";
            s_strings["tab_errordashboard"] = "Error Dashboard";
            s_strings["tab_performancedashboard"] = "Performance";
            s_strings["tab_networkdashboard"] = "Network";
            s_strings["tab_contextinspector"] = "Context Inspector";

            s_strings["action_error_dashboard_title"] = "Error Dashboard";
            s_strings["action_error_dashboard_desc"] = "Centralized error collection and monitoring";
            s_strings["action_performance_dashboard_title"] = "Performance Dashboard";
            s_strings["action_performance_dashboard_desc"] = "Real-time performance metrics and monitoring";
            s_strings["action_network_dashboard_title"] = "Network Dashboard";
            s_strings["action_network_dashboard_desc"] = "Network event tracking and latency monitoring";

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
            s_strings["wizard_subtab_create_root"] = "Create Root";
            s_strings["wizard_subtab_service_gen"] = "Service Gen";
            s_strings["wizard_subtab_view_gen"] = "View/Mediator Gen";
            s_strings["wizard_subtab_signal_gen"] = "Signal/Cmd Gen";
            s_strings["wizard_subtab_clean_deletion"] = "Clean Deletion";
            s_strings["wizard_section_manifest"] = "BOOTSTRAP MANIFEST GENERATION";
            s_strings["wizard_section_create_root"] = "CUSTOM ROOT CONTEXT CREATION";
            s_strings["wizard_section_view_gen"] = "GENERATE VIEW & MEDIATOR";
            s_strings["wizard_section_signal_gen"] = "GENERATE SIGNAL & COMMAND";
            s_strings["wizard_section_service_gen"] = "GENERATE SERVICE";
            s_strings["wizard_section_binding_help"] = "SERVICE BINDING HELP";
            s_strings["wizard_section_delete"] = "CLEAN DELETION TOOL";
            s_strings["wizard_section_dead_code"] = "DEAD CODE CLEANER";
            s_strings["wizard_field_context_name"] = "Context Name";
            s_strings["wizard_field_scope_tag"] = "Scope Tag";
            s_strings["wizard_field_scripts_folder"] = "Scripts Folder";
            s_strings["wizard_field_settings_folder"] = "Settings Folder";
            s_strings["wizard_field_parent_root"] = "Parent Root";
            s_strings["wizard_field_view_name"] = "View Name";
            s_strings["wizard_field_target_root"] = "Target Root Context";
            s_strings["wizard_field_signal_name"] = "Signal Name";
            s_strings["wizard_field_command_name"] = "Command Name";
            s_strings["wizard_field_service_name"] = "Service Name";
            s_strings["wizard_field_root_delete"] = "Root Context to Delete";
            s_strings["wizard_browse"] = "Browse";
            s_strings["wizard_toggle_lifecycle"] = "Create Lifecycle Template";
            s_strings["wizard_toggle_boilerplate"] = "Create Architecture Boilerplate";
            s_strings["wizard_foldout_modules"] = "Game Factory Core Modules";
            s_strings["wizard_toggle_iap"] = "In-App Purchases (IAP)";
            s_strings["wizard_toggle_ads"] = "Ads Network";
            s_strings["wizard_toggle_analytics"] = "Analytics";
            s_strings["wizard_toggle_inventory"] = "Inventory / Economy";
            s_strings["wizard_toggle_create_go"] = "Create GameObject in Scene";
            s_strings["wizard_hint_no_manifest"] = "No NexusBootstrapManifest found in the project. Create one to enable skeleton generation.";
            s_strings["wizard_label_active_manifest"] = "Active Manifest: {0}";
            s_strings["wizard_label_default_contexts"] = "Default Contexts: {0}";
            s_strings["wizard_hint_service_desc"] = "Generates an INexusService interface + NexusService<T> implementation with InitializeAsync and OnDispose lifecycle hooks.";
            s_strings["wizard_hint_binding_help"] = "In your Lifecycle's OnConfigure(IContextBuilder builder):\n  builder.BindService<I{ServiceName}, {ServiceName}>();\n\nNexus auto-initializes all services in registration order after Configure().\nServices are disposed in reverse order when the context is torn down.";
            s_strings["wizard_warning_delete"] = "WARNING: This will permanently delete:\n- The Root GameObject from the active scene.\n- The associated ContextData ScriptableObject.\n- The generated script directory under Assets/Scripts/Nexus/<ContextName>/\n\nMake sure you have backed up your custom script changes before committing!";
            s_strings["wizard_scanner_none"] = "No completely unused signals found.";
            s_strings["wizard_scanner_title"] = "Potentially dead signals (no references found outside definition):";
            s_strings["wizard_browse_title"] = "Select Folder";

            s_strings["hierarchy_title"] = "HIERARCHY GRAPH & DI DATA INSPECTOR";
            s_strings["hierarchy_di_inspector"] = "DI CONTAINER INSPECTOR";
            s_strings["hierarchy_none_resolved"] = "  None resolved.";
            s_strings["hierarchy_no_fields"] = "No fields or properties available.";
            s_strings["hierarchy_fields"] = "Fields";
            s_strings["hierarchy_properties"] = "Properties";
            s_strings["hierarchy_offline_title"] = "NEXUS CONTEXT GRAPH — OFFLINE";
            s_strings["hierarchy_offline_desc"] = "No active Nexus Contexts found. Enter Play Mode to inspect context hierarchy, parent-child relationships, and resolved DI singletons.";
            s_strings["hierarchy_roots_detected"] = "SCENE ROOTS DETECTED ({0})";
            s_strings["hierarchy_roots_desc"] = "Found {0} Root GameObject(s) in the scene. These will initialize Contexts in Play Mode.";
            s_strings["hierarchy_handlers_pill"] = "{0} Handlers";
            s_strings["hierarchy_config_so"] = "Config SO";
            s_strings["hierarchy_empty_playmode"] = "Enter Play Mode to inspect DI Container details.";
            s_strings["hierarchy_empty_select"] = "Select a Context card on the left panel to inspect its resolved dependencies.";
            s_strings["hierarchy_empty_no_data"] = "No resolved singletons or models found in this context's container.";
            s_strings["hierarchy_search_filter"] = "Search Filter";
            s_strings["hierarchy_context_fallback"] = "Context";

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
            s_strings["tracer_debug_disabled_title"] = "CAUSAL TRACING: NEXUS_DEBUG DISABLED";
            s_strings["tracer_debug_disabled_desc"] = "Full causal tracing (event trees, parent/child chains) is compiled out.\nBasic production trace is active below — showing recent signal dispatches.";
            s_strings["graph_overflow_desc"] = "{0} nodes exceed the {1} limit.\nConsider splitting your architecture into multiple smaller contexts,\nor use the Signal Explorer for text-based inspection.";

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
            s_strings["gamemanager_quick_actions"] = "QUICK ACTIONS";
            s_strings["gamemanager_stat_contexts"] = "Contexts";
            s_strings["gamemanager_stat_models"] = "Models";
            s_strings["gamemanager_stat_signals"] = "Signals";
            s_strings["gamemanager_stat_commands"] = "Commands";
            s_strings["gamemanager_stat_views"] = "Views";
            s_strings["gamemanager_stat_services"] = "Services";
            s_strings["gamemanager_stat_roots"] = "Scene Roots";
            s_strings["gamemanager_desc_contexts"] = "Active runtime contexts";
            s_strings["gamemanager_desc_models"] = "IReactiveModel implementations";
            s_strings["gamemanager_desc_signals"] = "Signal structs (ending in 'Signal')";
            s_strings["gamemanager_desc_commands"] = "Command bindings (attribute + fluent)";
            s_strings["gamemanager_desc_views"] = "View subclasses";
            s_strings["gamemanager_desc_services"] = "INexusService implementations";
            s_strings["gamemanager_desc_roots"] = "Root GameObjects in scene";
            s_strings["gamemanager_pill_child"] = "Child";
            s_strings["gamemanager_pill_root"] = "Root";
            s_strings["gamemanager_pill_tag"] = "Tag: {0}";
            s_strings["gamemanager_pill_cfg"] = "Cfg: {0}";
            s_strings["gamemanager_pill_parent"] = "Parent: {0}";
            s_strings["gamemanager_unnamed"] = "(unnamed)";
            s_strings["gamemanager_live_title"] = "LIVE MODEL & PERFORMANCE INSPECTOR";
            s_strings["gamemanager_live_playmode"] = "Enter Play Mode to inspect live model values and performance metrics.";
            s_strings["gamemanager_empty_signals"] = "No registered signal types found. Register commands for your signals first.";
            s_strings["gamemanager_metric_signals_s"] = "Signals/s";
            s_strings["gamemanager_metric_commands_s"] = "Commands/s";
            s_strings["gamemanager_metric_total_signals"] = "Total Signals";
            s_strings["gamemanager_metric_total_cmds"] = "Total Cmds";
            s_strings["gamemanager_section_overview"] = "Overview";
            s_strings["gamemanager_section_contexts"] = "Contexts";
            s_strings["gamemanager_section_models"] = "Models";
            s_strings["gamemanager_section_signals"] = "Signals";
            s_strings["gamemanager_section_commands"] = "Commands";
            s_strings["gamemanager_section_views"] = "Views";
            s_strings["gamemanager_section_services"] = "Services";
            s_strings["gamemanager_section_live"] = "Live";
            s_strings["gamemanager_section_test"] = "Test";
            s_strings["explorer_tab_signals"] = "Signal Explorer";
            s_strings["explorer_tab_models"] = "Live Models";

            s_strings["typeanalyzer_title"] = "TYPE COUPLING ANALYZER";
            s_strings["typeanalyzer_analyze"] = "Analyze";
            s_strings["typeanalyzer_not_found"] = "Could not find type '{0}' in active assemblies.";
            s_strings["typeanalyzer_dependencies"] = "Dependencies (Required Injections):";
            s_strings["typeanalyzer_dependents"] = "Referenced By (Dependents):";
            s_strings["typeanalyzer_no_deps"] = "No [Inject] dependencies found.";
            s_strings["typeanalyzer_no_dependents"] = "No other types are injecting this type.";

            s_strings["help_title"] = "NEXUS HELP & DOCUMENTATION";
            s_strings["help_version"] = "com.nexus.core v0.4.0";
            s_strings["help_platform"] = "Unity 6 (6000.x) | C# 9+ | .NET Standard 2.1 | UI Toolkit";
            s_strings["help_whats_new"] = "New in v0.4.0: Composite trigger payloads (CompositeContext), live command-pool\nutilization stats, execution-order guarantees, and expanded editor live panels.\nRequires Unity 6000.5 or higher.";
            s_strings["help_import_sample"] = "Import Counter Sample";
            s_strings["help_quickstart"] = "QUICK START";
            s_strings["help_coreapi"] = "CORE API";
            s_strings["help_version_section"] = "VERSION";
            s_strings["help_samples"] = "SAMPLES";
            s_strings["help_samples_hint"] = "The Counter example demonstrates the full Nexus flow: OnConfigure → Bindings → OnInitializeAsync → OnStartAsync → Signal Dispatch → Command Execution → View Update.";
            s_strings["help_step1_title"] = "1. Create a Root";
            s_strings["help_step1_desc"] = "GameObject → Nexus → Create Root (or use the Wizard tab).\nThis creates a Root GameObject + ContextData ScriptableObject in the scene.";
            s_strings["help_step2_title"] = "2. Define a Signal & Model";
            s_strings["help_step2_desc"] = "Create a signal struct and a model interface/class pair.\nModels can implement IReactiveModel for auto-notification via ObservableProperty<T>.";
            s_strings["help_step3_title"] = "3. Write a Lifecycle";
            s_strings["help_step3_desc"] = "Create a class named {ScopeTag}Lifecycle implementing IContextLifecycle.\nNexus auto-discovers it. Use OnConfigure() to bind models, commands, and services in one place.";
            s_strings["help_step4_title"] = "4. Create Commands";
            s_strings["help_step4_desc"] = "Implement ICommand<TSignal> or IAsyncCommand<TSignal>.\nBind in lifecycle: builder.BindSignal<MySignal>().To<MyCommand>();\nCommands run after signal dispatch, in the registered execution order.";
            s_strings["help_step5_title"] = "5. Wire Views & Mediators";
            s_strings["help_step5_desc"] = "Extend View, add [Mediator(typeof(MyMediator))], create Mediator<MyView>.\nUse Subscribe<TSignal>() in OnBind() to react to signals after the context has been configured.";
            s_strings["help_step6_title"] = "6. Fire Signals";
            s_strings["help_step6_desc"] = "SignalBus.Fire(new MySignal(data)) — from mediators, commands, or any [Inject]ed class.\nThis is the runtime entry point for signal → command execution.";
            s_strings["help_card_signalbus"] = "SignalBus";
            s_strings["help_card_signalbus_content"] = "Fire<T>(T signal) — synchronous dispatch\nFireAsync<T>(T signal) — awaitable async dispatch\nFireAsyncWithTimeout<T>(T, ms) — with timeout\nFireAsyncAndForget<T>(T, onError?) — fire-and-forget\nFireThreadSafe<T>(T) — from any thread\nFireNextFrame<T>(T) — deferred to next frame\nSubscribe<T>(Action<T>) / SubscribeAsync<T>(Func<T,CT,ValueTask>)\nOrder: dispatch → handlers → commands → model/view updates.";
            s_strings["help_card_contextbuilder"] = "ContextBuilder";
            s_strings["help_card_contextbuilder_content"] = "BindModel<T,I>() / BindReactiveModel<T,I>() — singleton models\nBindService<T,I>() — managed services with lifecycle\nBindSignal<T>().To<TCmd>() — fluent command binding\nBindCommand<T,TCmd>(mode, priority) — imperative binding\nBindAsyncCommand<T,TCmd>(mode, priority) — async binding\nEverything here belongs in OnConfigure().";
            s_strings["help_card_execmodes"] = "Execution Modes";
            s_strings["help_card_execmodes_content"] = "Sequential — default, priority-ordered, one at a time\nConcurrent — parallel async execution\nExclusive — single-handler guarantee\nComposite — fan-in: waits for multiple signals\nChoose the mode to match the command chain you want to read and reason about.";
            s_strings["help_card_attributes"] = "Attributes";
            s_strings["help_card_attributes_content"] = "[SignalHandler(typeof(T))] — auto-register command\n[CompositeSignalHandler(T1, T2)] — fan-in trigger\n[CrossContext(ScopeTag?)] — cross-context signal\n[Inject] — DI injection point\n[Mediator(typeof(T))] — view-mediator binding\n[LiveReload] — Play Mode asset sync\n[CommandTimeout(ms)] — async command timeout\nAttributes and fluent bindings are both visible in the Signal Explorer.";
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
