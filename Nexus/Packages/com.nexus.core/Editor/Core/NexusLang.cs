using System.Collections.Generic;
using UnityEngine;
using System.IO;

namespace Nexus.Editor
{
    internal static class NexusLang
    {
        private static Dictionary<string, string> s_strings;
        private static readonly string[] s_supportedLocales = { "en", "tr" };
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

        /// <summary>
        /// Retrieves the localized string for the specified key in the active language (English or Turkish).
        /// Falls back to the key string if the key is not found in the dictionary.
        /// </summary>
        /// <param name="key">The localization key identifier.</param>
        /// <returns>The localized text string.</returns>
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
            s_strings["action_fsm_title"] = "State Machine";
            s_strings["action_fsm_desc"] = "Live view of IGameStateMachine state, transitions & history";
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
            s_strings["tab_fsm"] = "State Machine";

            s_strings["cat_overview"] = "Overview";
            s_strings["cat_architecture"] = "Architecture";
            s_strings["cat_diagnostics"] = "Diagnostics";
            s_strings["cat_tools"] = "Tools";
            s_strings["cat_other"] = "Other";

            // NexusWindow — sidebar, status bar, discovery, actions
            s_strings["window_title"] = "Nexus Dashboard";
            s_strings["brand_title"] = "NEXUS";
            s_strings["brand_subtitle"] = "Architecture Suite";
            s_strings["sidebar_discovery_failed"] = "Plugin discovery failed: {0}";
            s_strings["sidebar_no_plugins"] = "No Nexus plugins found";
            s_strings["discovery_diagnostics"] = "Plugin discovery partially failed:\n{0}";
            s_strings["error_plugin_view"] = "Error loading plugin view: {0}";
            s_strings["actions_label"] = "ACTIONS ({0})";
            s_strings["status_play_mode_active"] = "PLAY MODE ACTIVE";
            s_strings["status_edit_mode"] = "EDIT MODE";
            s_strings["statusbar_play"] = "Nexus ● ACTIVE  |  {0} context(s) active  |  {1} static handler(s) registered";
            s_strings["statusbar_standby"] = "Nexus ○ STANDBY  |  {0} Root(s) in scene  |  Enter Play Mode to activate";

            // Dashboard plugin
            s_strings["dash_action_codegen"] = "⚡ CodeGen";
            s_strings["dash_action_create_root"] = "➕ Create Root";
            s_strings["dash_action_inspector"] = "🔍 Inspector";
            s_strings["dash_action_gamemanager"] = "📊 GameManager";
            s_strings["dash_quickfind_tooltip"] = "Search signals, commands, models, services, and views.";
            s_strings["dash_qf_copy"] = "Copy";
            s_strings["dash_qf_no_matches"] = "No matches for '{0}'";

            // Performance Dashboard plugin
            s_strings["pd_value_placeholder"] = "—";
            s_strings["pd_unit_mb"] = " MB";
            s_strings["pd_gc_gen0"] = "gen0 +{0:F0}";

            // Network Dashboard plugin
            s_strings["nd_title"] = "🌐 NETWORK MONITOR";
            s_strings["nd_action_clear"] = "🗑 Clear Log";
            s_strings["nd_action_export"] = "💾 Export Log";
            s_strings["nd_section_connection"] = "🔗 Connection Status";
            s_strings["nd_disconnected"] = "● DISCONNECTED";
            s_strings["nd_connected"] = "● CONNECTED";
            s_strings["nd_section_latency"] = "📡 Latency";
            s_strings["nd_latency_default"] = "— ms";
            s_strings["nd_latency_range"] = "0 — 500 ms";
            s_strings["nd_section_stats"] = "📊 Statistics";
            s_strings["nd_stat_sent"] = "Sent";
            s_strings["nd_stat_received"] = "Received";
            s_strings["nd_stat_errors"] = "Errors";
            s_strings["nd_filter_type"] = "Type:";
            s_strings["nd_filter_all"] = "All";
            s_strings["nd_filter_sent"] = "Sent";
            s_strings["nd_filter_received"] = "Received";
            s_strings["nd_filter_failed"] = "Failed";
            s_strings["nd_filter_timeout"] = "Timeout";
            s_strings["nd_filter_search"] = "  Search:";
            s_strings["nd_autoscroll"] = "Auto Scroll";
            s_strings["nd_section_events"] = "📜 Event Log (last 200)";
            s_strings["nd_no_events"] = "No network events recorded";
            s_strings["nd_col_type"] = "Type";
            s_strings["nd_col_signal"] = "Signal";
            s_strings["nd_col_direction"] = "Direction";
            s_strings["nd_col_time"] = "Time";
            s_strings["nd_dir_out"] = "→ Out";
            s_strings["nd_dir_in"] = "← In";
            s_strings["nd_dir_err"] = "⚠ Err";

            // Hierarchy plugin
            s_strings["hier_action_select_root"] = "🎯 Select Root";
            s_strings["hier_action_inspector"] = "🔍 Context Inspector";
            s_strings["hier_action_clear_caches"] = "🧹 Clear Caches";
            s_strings["hier_context_label"] = "CONTEXT: {0}";
            s_strings["hier_default_tag"] = "Default";
            s_strings["hier_force_gc"] = "🗑️ Force GC";
            s_strings["hier_reset_contexts"] = "🧹 Reset Contexts";

            // Explorer plugin
            s_strings["exp_action_codegen"] = "⚡ CodeGen";
            s_strings["exp_action_inspector"] = "🔍 Inspector";
            s_strings["exp_action_rescan"] = "🔄 Rescan";
            s_strings["exp_all_assemblies"] = "All Assemblies";
            s_strings["exp_refresh_cache"] = "Refresh Cache";
            s_strings["exp_badge_async"] = "ASYNC";
            s_strings["exp_btn_copy"] = "📋";
            s_strings["exp_tooltip_copy"] = "Copy Signal Name";
            s_strings["exp_btn_open"] = "🔍";
            s_strings["exp_tooltip_open"] = "Open Script in IDE";
            s_strings["exp_preset_default"] = "Default";
            s_strings["exp_btn_save"] = "Save";
            s_strings["exp_no_presets"] = "No Presets";
            s_strings["exp_btn_load"] = "Load";

            // Graph plugin
            s_strings["graph_max_nodes"] = "Max Nodes";
            s_strings["graph_port_output"] = "▶";
            s_strings["graph_port_input"] = "◀";

            // Tracer plugin
            s_strings["tracer_clear"] = "Clear";
            s_strings["tracer_sig"] = "SIG";
            s_strings["tracer_cmd"] = "CMD";
            s_strings["tracer_mod"] = "MOD";
            s_strings["tracer_ok"] = "OK";
            s_strings["tracer_fail"] = "FAIL";
            s_strings["tracer_cancel"] = "CANCEL";
            s_strings["tracer_time_suffix"] = "s";

            // GameManager plugin
            s_strings["gm_quick_find"] = "Quick Find";
            s_strings["gm_quick_find_tooltip"] = "Type a section name such as contexts, signals, models, services, live";
            s_strings["gm_contexts_header"] = "CONTEXTS ({0} active)";
            s_strings["gm_roots_hint"] = "\nScene Roots: {0} Root GameObject(s) in scene.";
            s_strings["gm_models_header"] = "MODELS ({0} registered)";
            s_strings["gm_signals_header"] = "SIGNALS ({0} defined)";
            s_strings["gm_commands_header"] = "COMMANDS ({0} bound)";
            s_strings["gm_views_header"] = "VIEWS ({0} defined)";
            s_strings["gm_services_header"] = "SERVICES ({0} registered)";
            s_strings["gm_playmode_only"] = " (Play Mode only)";
            s_strings["gm_gc_alloc"] = "GC Alloc";
            s_strings["gm_contexts_metric"] = "Contexts";
            s_strings["gm_sig_per_sec"] = "Sig/s";
            s_strings["gm_cmd_per_sec"] = "Cmd/s";
            s_strings["gm_signal_test_panel"] = "SIGNAL TEST PANEL";
            s_strings["gm_all_matching"] = "All (matching)";
            s_strings["gm_result_error"] = "✘ {0}: no active context handles this signal.";
            s_strings["gm_result_success"] = "✔ Fired {0} into {1} context(s) @ {2:HH:mm:ss}";

            // FSM plugin
            s_strings["fsm_fallback_context"] = "context";
            s_strings["fsm_no_state"] = "(none)";

            // CasualServices plugin
            s_strings["cs_default_currency"] = "Coins";
            s_strings["cs_default_window"] = "ShopScreen";
            s_strings["cs_destroyed_suffix"] = " (destroyed)";

            // Context Inspector plugin
            s_strings["ci_title"] = "🔍 CONTEXT INSPECTOR";
            s_strings["ci_playmode_warning"] = "⚠ Enter Play Mode to inspect live contexts";
            s_strings["ci_context_label"] = "Context:";
            s_strings["ci_none_editmode"] = "(none — edit mode)";
            s_strings["ci_playmode_prompt"] = "Start Play Mode to inspect live contexts.";
            s_strings["ci_select_context"] = "Select a context from the dropdown above.";
            s_strings["ci_tab_overview"] = "Overview";
            s_strings["ci_tab_bindings"] = "Bindings";
            s_strings["ci_tab_singletons"] = "Singletons";
            s_strings["ci_tab_services"] = "Services";
            s_strings["ci_tab_signals"] = "Signals";
            s_strings["ci_tab_extensions"] = "🔌 Extensions";
            s_strings["ci_tab_firesignal"] = "🔥 Fire Signal";
            s_strings["ci_action_refresh"] = "🔄 Refresh";
            s_strings["ci_action_copy_report"] = "📋 Copy Report";
            s_strings["ci_overview_title"] = "📋 Context Overview";
            s_strings["ci_stat_tag"] = "Tag";
            s_strings["ci_stat_type"] = "Type";
            s_strings["ci_stat_parent"] = "Parent";
            s_strings["ci_stat_di_bindings"] = "DI Bindings";
            s_strings["ci_stat_singletons"] = "Singletons";
            s_strings["ci_stat_signals"] = "Signals";
            s_strings["ci_stat_plugins"] = "Plugins";
            s_strings["ci_stat_has_interceptors"] = "Has Interceptors";
            s_strings["ci_stat_child_contexts"] = "Child Contexts";
            s_strings["ci_no_tag"] = "(no tag)";
            s_strings["ci_none"] = "none";
            s_strings["ci_runtime_plugins"] = "🔌 Runtime Plugins";
            s_strings["ci_stat_plugin"] = "  Plugin";
            s_strings["ci_signal_queues"] = "📨 Signal Queues";
            s_strings["ci_ts_depth"] = "Thread-Safe Depth";
            s_strings["ci_nf_depth"] = "Next-Frame Depth";
            s_strings["ci_total_enqueued"] = "Total Enqueued";
            s_strings["ci_total_drained"] = "Total Drained";
            s_strings["ci_pending"] = "Pending (in-flight)";
            s_strings["ci_command_pools"] = "♻️ Command Pools";
            s_strings["ci_pooled_types"] = "Pooled Types";
            s_strings["ci_available_now"] = "Available Now";
            s_strings["ci_total_gets"] = "Total Gets";
            s_strings["ci_total_created"] = "Total Created";
            s_strings["ci_total_returns"] = "Total Returns";
            s_strings["ci_total_discarded"] = "Total Discarded";
            s_strings["ci_reuse_ratio"] = "Reuse Ratio";
            s_strings["ci_ext_pipeline"] = "🔌 Extension Pipeline";
            s_strings["ci_ext_empty"] = "No runtime plugins registered on this context.\nSignal interceptors and command decorators appear here once a plugin registers them.";
            s_strings["ci_signal_interceptors"] = "Signal Interceptors";
            s_strings["ci_command_decorators"] = "Command Decorators";
            s_strings["ci_command_decorators_order"] = "Command Decorators (execution order)";
            s_strings["ci_model_serializers"] = "Model Serializers";
            s_strings["ci_trace_sinks"] = "Trace Sinks";
            s_strings["ci_no_plugin_context"] = "(no plugin context)";
            s_strings["ci_di_bindings_title"] = "🔗 DI Bindings";
            s_strings["ci_no_bindings"] = "No bindings found. (Play Mode required)";
            s_strings["ci_col_interface_key"] = "Interface / Key";
            s_strings["ci_col_concrete_type"] = "Concrete Type";
            s_strings["ci_total_bindings"] = "Total: {0} binding(s)";
            s_strings["ci_resolved_singletons_title"] = "📦 Resolved Singletons";
            s_strings["ci_no_singletons"] = "No singletons resolved yet.";
            s_strings["ci_implements"] = "  implements: {0}";
            s_strings["ci_total_singletons"] = "Total: {0} singleton(s)";
            s_strings["ci_registered_services_title"] = "⚙️ Registered Services";
            s_strings["ci_no_services"] = "No services registered in this context.";
            s_strings["ci_not_resolved"] = "not resolved";
            s_strings["ci_col_service_type"] = "Service Type";
            s_strings["ci_col_concrete"] = "Concrete";
            s_strings["ci_col_resolved"] = "Resolved";
            s_strings["ci_total_services"] = "Total: {0} service(s)";
            s_strings["ci_signal_handlers_title"] = "⚡ Registered Signal Handlers";
            s_strings["ci_no_signal_handlers"] = "No signal handlers registered in this context.";
            s_strings["ci_col_signal"] = "Signal";
            s_strings["ci_col_command_handler"] = "Command Handler";
            s_strings["ci_col_mode"] = "Mode";
            s_strings["ci_total_signals_count"] = "Total signals: {0}";
            s_strings["ci_fire_test_signal_title"] = "🔥 Fire Test Signal";
            s_strings["ci_select_context_first"] = "Select a context first.";
            s_strings["ci_no_signal_types"] = "No signal types found in loaded assemblies.";
            s_strings["ci_signal_type"] = "Signal Type";
            s_strings["ci_select_signal_type"] = "(select a signal type)";
            s_strings["ci_cannot_instantiate"] = "Cannot instantiate signal (no default constructor).";
            s_strings["ci_no_public_fields"] = "(no public fields — signal is parameter-less)";
            s_strings["ci_fill_fields"] = "Fill in signal fields:";
            s_strings["ci_not_editable"] = "({0} — not editable)";
            s_strings["ci_err_no_context_signal"] = "❌ No context or signal selected.";
            s_strings["ci_fired_ok"] = "✅ Fired {0} at {1}";
            s_strings["ci_err_fire_notfound"] = "❌ Fire<T> method not found on SignalBus.";
            s_strings["ci_err_generic"] = "❌ Error: {0}";
            s_strings["ci_fire_btn"] = "🔥 Fire {0}";

            // ── FSM plugin ──────────────────────────────────────
            s_strings["fsm_toolbar"] = "STATE MACHINE";
            s_strings["fsm_empty_playing"] = "No IGameStateMachine resolved from active contexts.";
            s_strings["fsm_empty_editmode"] = "Enter Play Mode to inspect live state machines.";
            s_strings["fsm_status"] = "Machines: {0}";
            s_strings["fsm_current_state"] = "Current State";
            s_strings["fsm_error_state"] = "Error State";
            s_strings["fsm_registered_states"] = "Registered States";
            s_strings["fsm_none"] = "(none)";
            s_strings["fsm_not_set"] = "(not set)";
            s_strings["fsm_custom_impl"] = "Custom IGameStateMachine implementation — only CurrentState is introspectable.";
            s_strings["fsm_transition_log"] = "Transition Log (observed)";

            // ── Casual Services plugin ──────────────────────────
            s_strings["cs_title"] = "Nexus Casual Services Debugger";
            s_strings["cs_editmode_prompt"] = "Enter Play Mode to debug Economy, Progression, UI, Audio, Haptics, and TimeScale live.";
            s_strings["cs_sec_timescale"] = "TimeScale & Loop Controls";
            s_strings["cs_time_scale"] = "Time Scale";
            s_strings["cs_toggle_pause"] = "Toggle Pause";
            s_strings["cs_sec_economy"] = "Economy Debugger";
            s_strings["cs_currency_id"] = "Currency ID";
            s_strings["cs_amount"] = "Amount";
            s_strings["cs_earn_currency"] = "Earn Currency";
            s_strings["cs_spend_currency"] = "Spend Currency";
            s_strings["cs_active_storage"] = "Active Storage: {0}";
            s_strings["cs_autosave"] = " (AutoSave: {0})";
            s_strings["cs_save_flush"] = "Save/Flush Storage to Disk";
            s_strings["cs_sec_progression"] = "Progression Debugger";
            s_strings["cs_jump_to_level"] = "Jump To Level";
            s_strings["cs_set_level"] = "Set Level";
            s_strings["cs_sec_ui"] = "UI Window Navigation";
            s_strings["cs_window_name"] = "Window Name";
            s_strings["cs_open_window"] = "Open Window";
            s_strings["cs_close_top"] = "Close Top Window";
            s_strings["cs_asset_provider"] = "UI Asset Provider: {0}";
            s_strings["cs_open_stack"] = "Open Window Stack (live)";
            s_strings["cs_sec_haptics"] = "Haptics & Feedback Tester";
            s_strings["cs_light_haptic"] = "Trigger Light Haptic";
            s_strings["cs_heavy_haptic"] = "Trigger Heavy Haptic";
            s_strings["cs_success_feedback"] = "Play Success Feedback";
            s_strings["cs_no_windowmanager"] = "  (no WindowManager registered)";
            s_strings["cs_custom_windowmanager"] = "  (custom IWindowManager — no introspection)";
            s_strings["cs_stack_header"] = "Open: {0}    Pending: {1}";
            s_strings["cs_stack_empty"] = "  (stack empty)";

            // ── Error Dashboard plugin ──────────────────────────
            s_strings["ed_severity"] = "Severity:";
            s_strings["ed_sev_all"] = "All";
            s_strings["ed_sev_info"] = "Info+";
            s_strings["ed_sev_warn"] = "Warn+";
            s_strings["ed_sev_error"] = "Error+";
            s_strings["ed_sev_critical"] = "Critical";
            s_strings["ed_category"] = "Category";
            s_strings["ed_filter_category"] = "Filter Category";
            s_strings["ed_search_placeholder"] = "Search message...";
            s_strings["ed_capture"] = "Capture";
            s_strings["ed_export_csv"] = "Export CSV";
            s_strings["ed_clear"] = "Clear";
            s_strings["ed_total"] = "TOTAL";
            s_strings["ed_info"] = "INFO";
            s_strings["ed_warn"] = "WARN";
            s_strings["ed_error"] = "ERROR";
            s_strings["ed_critical"] = "CRITICAL";
            s_strings["ed_empty"] = "No errors match the current filters.";
            s_strings["ed_status"] = "Showing {0} of {1} (limit {2})   |   Capture: {3}";
            s_strings["ed_on"] = "ON";
            s_strings["ed_off"] = "OFF";
            s_strings["ed_context_prefix"] = "Context: {0}";

            // ── Performance Dashboard plugin ─────────────────
            s_strings["pd_toolbar"] = "⚡ PERFORMANCE MONITOR";
            s_strings["pd_start_recording"] = "▶ Start Recording";
            s_strings["pd_stop"] = "⏹ Stop";
            s_strings["pd_clear"] = "🗑 Clear";
            s_strings["pd_export_csv"] = "💾 Export CSV";
            s_strings["pd_sample"] = "Sample: 0.5 s";
            s_strings["pd_alarms"] = "Alarms";
            s_strings["pd_sec_frame"] = "📊 Frame Metrics";
            s_strings["pd_fps"] = "FPS";
            s_strings["pd_sec_memory"] = "🧠 Memory";
            s_strings["pd_mono_heap"] = "Mono Heap (MB)";
            s_strings["pd_gc_gen0"] = "GC Gen0";
            s_strings["pd_sec_throughput"] = "⚡ Nexus Throughput";
            s_strings["pd_signals_per_s"] = "Signals/s";
            s_strings["pd_commands_per_s"] = "Commands/s";
            s_strings["pd_metrics_note"] = "Metrics collected via Nexus runtime event hooks.";
            s_strings["pd_sec_alarms"] = "🔔 Alarm Thresholds";
            s_strings["pd_fps_alarm"] = "FPS Alarm (<)";
            s_strings["pd_mem_alarm"] = "Memory Alarm (MB >)";
            s_strings["pd_sec_summary"] = "📋 Summary (Last Sample)";
            s_strings["pd_fps_below"] = "⚠ FPS BELOW THRESHOLD ({0} < {1})";
            s_strings["pd_mem_above"] = "⚠ MEMORY ABOVE THRESHOLD ({0} MB > {1} MB)";
            s_strings["pd_fps_current"] = "FPS (current)";
            s_strings["pd_fps_avg"] = "FPS (avg 60s)";
            s_strings["pd_fps_min"] = "FPS (min 60s)";
            s_strings["pd_mono_heap_short"] = "Mono Heap";
            s_strings["pd_signals_current"] = "Signals/s (current)";
            s_strings["pd_commands_current"] = "Commands/s (current)";
            s_strings["pd_gc_delta"] = "GC Gen0 (delta)";

            // ── Cross-plugin navigation & stray labels ─────────
            s_strings["nav_open_hierarchy"] = "Open Hierarchy";
            s_strings["nav_open_explorer"] = "Open Explorer";
            s_strings["nav_open_tracer"] = "Open Tracer";
            s_strings["nav_open_gamemanager"] = "Open Game Manager";
            s_strings["nav_open_typeanalyzer"] = "Open Type Analyzer";
            s_strings["common_refresh"] = "Refresh";
            s_strings["clear"] = "Clear";
            s_strings["ex_refresh_targets"] = "Refresh Targets";
            s_strings["gm_go"] = "Go";
            s_strings["gm_refresh_all"] = "Refresh All";
            s_strings["gm_no_contexts"] = "No active contexts. Enter Play Mode to activate.";
            s_strings["gm_no_reactive_models"] = "No IReactiveModel implementations found.";
            s_strings["tr_pause"] = "Pause";
            s_strings["gm_tip_reactive"] = "Tip: Implement IReactiveModel on your models to enable live inspection here.";
            s_strings["gm_no_signals"] = "No signal structs found.";
            s_strings["gm_pill_unhandled"] = "unhandled";
            s_strings["gm_no_commands"] = "No command bindings found.";
            s_strings["gm_no_views"] = "No View subclasses found.";
            s_strings["gm_no_services"] = "No INexusService implementations found.";
            s_strings["gm_tip_services"] = "Tip: Use builder.BindService<TInterface, TImpl>() to register services in your lifecycle.";
            s_strings["gm_no_active_contexts"] = "No active contexts.";
            s_strings["gm_hint_live_model"] = "Live model inspection resolves IReactiveModel instances. Open the Live Tracer for real-time signal monitoring.";
            s_strings["gm_enter_playmode_fire"] = "Enter Play Mode to fire test signals.";
            s_strings["gm_no_contexts_fire"] = "No active contexts to fire signals into.";
            s_strings["gm_hint_fire"] = "Select a target context (or 'All (matching)') above, then click any signal to fire it. Use the Explorer tab for signals with custom payloads.";
            s_strings["db_quick_find"] = "Quick Find";
            s_strings["db_nexus_health"] = "Nexus Health";
            s_strings["db_health_note"] = "Use this panel to catch missing Roots, empty Contexts, and validation issues before handoff.";
            s_strings["db_validation_note"] = "Validation checks context, binding, hierarchy, and command issues before runtime.";
            s_strings["dash_tip_contexts"] = "Open the Contexts view";
            s_strings["dash_tip_handlers"] = "Open signal handlers in Explorer";
            s_strings["dash_tip_roots"] = "Focus scene roots in Game Manager";
            s_strings["dash_tip_models"] = "Open the Models section";
            s_strings["dash_tip_services"] = "Open the Services section";
            s_strings["dash_tip_commands"] = "Open the Commands section";
            s_strings["dash_tip_views"] = "Open the Views section";

            // Tracer detail panel
            s_strings["tr_ctx_clear_buffer"] = "🧹 Clear Buffer";
            s_strings["tr_ctx_pause"] = "⏸ Pause";
            s_strings["tr_ctx_inspector"] = "🔍 Inspector";
            s_strings["tr_detail_event_id"] = "Event #{0}";
            s_strings["tr_detail_type"] = "Type: {0}";
            s_strings["tr_detail_name"] = "Name: {0}";
            s_strings["tr_detail_status"] = "Status: {0}";
            s_strings["tr_detail_mode"] = "Mode: {0}";
            s_strings["tr_detail_time_label"] = "Time: ";
            s_strings["tr_detail_parent_id_label"] = "Parent ID: ";
            s_strings["tr_detail_none_root"] = "None (root)";
            s_strings["tr_detail_parent_event"] = "\n<b>Parent Event:</b> #{0} [{1}] {2}";
            s_strings["tr_detail_children"] = "\n<b>Children ({0}):</b>";
            s_strings["tr_detail_child_row"] = "  #{0} [{1}] {2} — {3}";
            s_strings["tr_tree_prefix"] = "└─ ";

            // ContextInspector
            s_strings["ci_dropdown_none"] = "(none)";
            s_strings["ci_null_label"] = "(null)";

            // Explorer kalan
            s_strings["exp_unnamed_context"] = "Unnamed Context";
            s_strings["exp_all_assemblies_fallback"] = "All Assemblies";
            s_strings["exp_target_context_label"] = "Target Context";
            s_strings["exp_tooltip_ctx_dropdown"] = "Fire the test signal into the selected context";
            s_strings["exp_tooltip_fire_btn"] = "Fire the selected signal into the target context";

            // Hierarchy kalan
            s_strings["hier_bullet"] = "• ";
            s_strings["hier_null_value"] = "null";

            // TypeAnalyzer
            s_strings["ta_type_name"] = "Type Name";
            s_strings["db_play_mode"] = "Play Mode";
            s_strings["db_edit_mode"] = "Edit Mode";
            s_strings["db_health_no_root"] = "No active Root in scene. Add a Nexus Root before entering Play Mode.";
            s_strings["db_health_no_context"] = "No active Context detected during Play Mode. Check startup wiring and scene bindings.";
            s_strings["db_health_counts"] = "{0} context(s), {1} handler(s), {2} root(s) visible.";
            s_strings["db_health_line"] = "{0}: {1}";
            s_strings["ta_enter_type"] = "Please enter a type name to analyze.";

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
