using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Editor
{
    internal static class NexusLang
    {
        private static Dictionary<string, string> s_strings;
        private static readonly string[] s_supportedLocales = { "en", "tr" };
        private static string s_currentLocale = "en";

        public static string CurrentLocale => s_currentLocale;

        private static string s_packageVersion;
        /// <summary>The installed package version from the Package Manager; the only version source the editor UI uses.</summary>
        public static string PackageVersion
        {
            get
            {
                if (s_packageVersion == null)
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusLang).Assembly);
                    s_packageVersion = package != null ? package.version : "unknown";
                }
                return s_packageVersion;
            }
        }
        public static IReadOnlyList<string> SupportedLocales => s_supportedLocales;

        static NexusLang()
        {
            s_currentLocale = UnityEditor.EditorPrefs.GetString("Nexus_Locale", "en");
            LoadLocale(s_currentLocale);
        }

        public static void LoadLocale(string locale)
        {
            locale = locale == "tr" ? "tr" : "en";
            s_currentLocale = locale;
            s_strings = new Dictionary<string, string>();

            // Default English fallback
            AddDefaults();

            // Try to load locale-specific overrides from JSON
            string path = $"Packages/com.nexus.core/Editor/Locales/{locale}.json";
            // AssetDatabase resolves virtual UPM paths for embedded, Git and tarball packages.
            // File.Exists on Packages/... works only for physically embedded packages.
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset != null)
            {
                try
                {
                    var json = asset.text;
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
            s_strings["setup_name"] = "Setup Wizard";
            s_strings["setup_title"] = "Nexus Setup Wizard";
            s_strings["setup_description"] = "Start a new project from the starter scene or a sample, or add a context to an existing scene. The documents explain each integration path.";
            s_strings["setup_scaffold"] = "Create a sample scene";
            s_strings["setup_scaffold_description"] = "Adds sample code under Assets/Scripts/Game/Samples, a ContextData asset and a counter scene. Existing output is preserved.";
            s_strings["setup_create"] = "Create sample";
            s_strings["setup_tip"] = "The counter follows Signal -> Command -> Model -> Mediator -> View. Existing games can start with one explicitly registered feature; no complete rewrite is required.";
            s_strings["setup_guide"] = "Choose your integration path";
            s_strings["setup_scaffold_exists"] = "Already generated: Assets/Scripts/Game/Samples, Assets/Scenes/NexusStarter.unity or Assets/GameContextData.asset exists, and existing files are kept.";
            s_strings["setup_samples"] = "Package samples";
            s_strings["setup_samples_description"] = "Each sample is copied into Assets/Samples.";
            s_strings["setup_sample_import"] = "Import";
            s_strings["setup_sample_imported"] = "Imported";
            s_strings["setup_existing"] = "Add Nexus to an existing scene";
            s_strings["setup_existing_description"] = "The Context Wizard creates a ContextData asset and a Root in the open scene, then generates services, views and signals for it.";
            s_strings["setup_docs"] = "Documentation";
            s_strings["setup_doc_quickstart"] = "10-minute quick start";
            s_strings["setup_doc_getting_started"] = "Getting started";
            s_strings["setup_doc_troubleshooting"] = "Troubleshooting";
            s_strings["setup_doc_changelog"] = "Changelog";
            s_strings["setup_version"] = "com.nexus.core {0} · Unity {1}";
            s_strings["system_active"] = "SYSTEM ACTIVE";
            s_strings["system_standby"] = "SYSTEM STANDBY";
            s_strings["tracer_status_ok"] = "OK";
            s_strings["tracer_status_fail"] = "FAIL";
            s_strings["tracer_status_cancel"] = "CANCEL";
            s_strings["contexts"] = "Contexts";
            s_strings["handlers"] = "Handlers";
            s_strings["roots"] = "Roots";
            s_strings["models"] = "Models";
            s_strings["services"] = "Services";
            s_strings["open"] = "Open";
            s_strings["pass"] = "PASS";
            s_strings["fail"] = "FAIL";
            s_strings["ready"] = "Ready. Enter Play Mode to activate the system.";
            s_strings["no_roots"] = "Create a Root via Context Wizard to get started.";
            s_strings["live_hint"] = "Live — {0} context(s) active.";
            s_strings["action_wizard_title"] = "Context Wizard";
            s_strings["action_tracer_title"] = "Live Tracer";
            s_strings["action_casual_services_title"] = "Casual Debugger";
            s_strings["action_fsm_title"] = "State Machine";
            s_strings["tab_status"] = "Status";
            s_strings["tab_wizard"] = "Wizard";
            s_strings["tab_tracer"] = "Tracer";
            s_strings["tab_errordashboard"] = "Error Dashboard";
            s_strings["tab_performancedashboard"] = "Performance";
            s_strings["tab_contextinspector"] = "Context Inspector";
            s_strings["tab_fsm"] = "State Machine";
            s_strings["tab_casual_services"] = "Casual Debugger";

            s_strings["cat_status"] = "Status";
            s_strings["cat_setup"] = "Setup";
            s_strings["cat_architecture"] = "Architecture";
            s_strings["cat_live"] = "Live";

            // Architecture tab and the signal fire form (#193 phase 3)
            s_strings["action_architecture_title"] = "Architecture";
            s_strings["arch_rule"] = "Signals are the TSignal of each ICommand<T> or IAsyncCommand<T> and the types [SignalHandler] and [CompositeSignalHandler] name. A signal with only Subscribe listeners, or a command bound in code to a signal it does not name, shows once a context runs.";
            s_strings["arch_summary_tooltip"] = "Click to show only this kind; click again to show every kind.";
            s_strings["arch_search"] = "Search";
            s_strings["arch_kind_all"] = "All kinds";
            s_strings["arch_kind_signal"] = "Signals";
            s_strings["arch_kind_command"] = "Commands";
            s_strings["arch_kind_service"] = "Services";
            s_strings["arch_kind_model"] = "Models";
            s_strings["arch_kind_view"] = "Views";
            s_strings["arch_kind_mediator"] = "Mediators";
            s_strings["arch_all_assemblies"] = "All assemblies";
            s_strings["arch_show_graph"] = "Show graph";
            s_strings["arch_show_map"] = "Show map";
            s_strings["arch_rescan"] = "Rescan";
            s_strings["arch_empty"] = "The game's assemblies declare no signal, command, service, model, view or mediator.";
            s_strings["arch_no_match"] = "Nothing matches the filter.";
            s_strings["arch_select"] = "Select a type on the left.";
            s_strings["arch_open_script"] = "Open script";
            s_strings["arch_no_script"] = "No script file is named after this type.";
            s_strings["arch_handled_by"] = "Handled by";
            s_strings["arch_no_handler"] = "No command in the game's code names this signal.";
            s_strings["arch_runtime_only"] = "bound at runtime";
            s_strings["arch_bound_in_code"] = "bound in code";
            s_strings["arch_handles"] = "Handles";
            s_strings["arch_no_signal"] = "No signal named in code: a BindCommand call binds this command, and its signal appears in the signal list while a context runs.";
            s_strings["arch_source_implements"] = "implements {0}";
            s_strings["arch_source_attribute"] = "[SignalHandler] {0}, priority {1}";
            s_strings["arch_source_composite"] = "[CompositeSignalHandler], priority {0}";
            s_strings["arch_source_register"] = "[RegisterCommand] {0}, priority {1}";
            s_strings["arch_source_register_composite"] = "[RegisterCompositeCommand], priority {0}";
            s_strings["arch_tag_async"] = "async";
            s_strings["arch_tag_oneshot"] = "one-shot";
            s_strings["arch_bound"] = "Bound in the running contexts";
            s_strings["arch_bound_none"] = "No running context binds it.";
            s_strings["arch_bound_row"] = "{0}: {1}, priority {2}";
            s_strings["arch_fire"] = "Fire";
            s_strings["arch_implements"] = "Implements";
            s_strings["arch_mediators"] = "Mediators";
            s_strings["arch_view"] = "View";
            s_strings["arch_dependencies"] = "Injected into it";
            s_strings["arch_no_dependencies"] = "Nexus injects nothing into it.";
            s_strings["arch_dependents"] = "Receives it";
            s_strings["arch_no_dependents"] = "No member in the game's code receives it.";
            s_strings["arch_optional"] = "optional";
            s_strings["arch_graph_max_nodes"] = "Max nodes";
            s_strings["arch_graph_empty"] = "No signal matches the filter.";
            s_strings["arch_graph_left_out"] = "{0} nodes are not drawn: raise Max nodes or narrow the filter.";
            s_strings["arch_graph_stats"] = "{0} signals, {1} edges. Double-click a node to open it in the map.";
            s_strings["fire_needs_play_mode"] = "Firing a signal needs Play Mode.";
            s_strings["fire_cannot_create"] = "{0} cannot be created: {1}";
            s_strings["fire_no_fields"] = "This signal has no public fields.";
            s_strings["fire_field_not_editable"] = "{0} ({1}) cannot be edited here.";
            s_strings["fire_no_context"] = "No context is running.";
            s_strings["fire_context"] = "Context";
            s_strings["fire_button"] = "Fire {0}";
            s_strings["fire_refresh_contexts"] = "Refresh contexts";
            s_strings["fire_untagged_context"] = "(no scope tag)";
            s_strings["fire_context_gone"] = "That context is no longer running; refresh the contexts.";
            s_strings["fire_running"] = "Firing {0} in {1}…";
            s_strings["fire_done"] = "{0} fired in {1}; every handler finished at {2}.";
            s_strings["fire_failed"] = "{0} failed: {1}: {2}";
            s_strings["fire_presets"] = "Presets";
            s_strings["fire_preset_default"] = "Default";
            s_strings["fire_preset_save"] = "Save";
            s_strings["fire_preset_load"] = "Load";
            s_strings["fire_no_presets"] = "No preset is saved for this signal.";
            s_strings["cat_other"] = "Other";

            // NexusWindow — sidebar, status bar, discovery, actions
            s_strings["brand_title"] = "NEXUS";
            s_strings["brand_subtitle"] = "Architecture Suite";
            s_strings["sidebar_discovery_failed"] = "Plugin discovery failed: {0}";
            s_strings["sidebar_no_plugins"] = "No Nexus plugins found";
            s_strings["error_plugin_view"] = "Error loading plugin view: {0}";
            s_strings["actions_label"] = "ACTIONS ({0})";
            s_strings["status_play_mode_active"] = "PLAY MODE ACTIVE";
            s_strings["status_edit_mode"] = "EDIT MODE";
            s_strings["statusbar_play"] = "Nexus ● ACTIVE  |  {0} context(s) active  |  {1} handler(s) registered";
            s_strings["statusbar_standby"] = "Nexus ○ STANDBY  |  {0} Root(s) in scene  |  Enter Play Mode to activate";

            // Performance Dashboard plugin
            s_strings["pd_value_placeholder"] = "—";
            s_strings["pd_unit_mb"] = " MB";
            // Value format for the big GC label (pd_gc_gen0 is the section title only).
            s_strings["pd_gc_gen0_value"] = "gen0 +{0:F0}";

            // Tracer plugin
            s_strings["tracer_clear"] = "Clear";
            s_strings["tracer_sig"] = "SIG";
            s_strings["tracer_cmd"] = "CMD";
            s_strings["tracer_mod"] = "MOD";
            s_strings["tracer_time_suffix"] = "s";

            // FSM plugin
            s_strings["fsm_fallback_context"] = "context";
            s_strings["fsm_no_state"] = "(none)";

            // CasualServices plugin
            s_strings["cs_default_currency"] = "Coins";
            s_strings["cs_destroyed_suffix"] = " (destroyed)";

            // Context Inspector plugin
            s_strings["ci_title"] = "🔍 CONTEXT INSPECTOR";
            s_strings["ci_playmode_prompt"] = "Start Play Mode to inspect live contexts.";
            s_strings["ci_tab_overview"] = "Overview";
            s_strings["ci_tab_bindings"] = "Bindings";
            s_strings["ci_tab_singletons"] = "Singletons";
            s_strings["ci_tab_services"] = "Services";
            s_strings["ci_tab_signals"] = "Signals";
            s_strings["ci_tab_extensions"] = "🔌 Extensions";
            s_strings["ci_tab_firesignal"] = "🔥 Fire Signal";
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
            s_strings["ci_no_signal_types"] = "No signal types found in loaded assemblies.";
            s_strings["ci_signal_type"] = "Signal Type";

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
            s_strings["cs_sec_ui"] = "UI Screen Navigation";
            s_strings["cs_close_top"] = "Close Top Screen";
            s_strings["cs_asset_provider"] = "UI Asset Provider: {0}";
            s_strings["cs_open_stack"] = "Open Screen Stack (live)";
            s_strings["cs_sec_haptics"] = "Haptics & Feedback Tester";
            s_strings["cs_light_haptic"] = "Trigger Light Haptic";
            s_strings["cs_heavy_haptic"] = "Trigger Heavy Haptic";
            s_strings["cs_success_feedback"] = "Play Success Feedback";
            s_strings["cs_no_uimanager"] = "  (no UIManager registered)";
            s_strings["cs_custom_uimanager"] = "  (custom IUIManager — no introspection)";
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
            s_strings["ed_copy_all"] = "📋 Copy All";
            s_strings["ed_copy_log"] = "📋 Copy Log";
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
            s_strings["pd_sample"] = "Sample: 0.6 s";
            s_strings["pd_alarms"] = "Alarms";
            s_strings["pd_sec_frame"] = "📊 Frame Metrics";
            s_strings["pd_fps"] = "FPS";
            s_strings["pd_sec_memory"] = "🧠 Memory";
            s_strings["pd_mono_heap"] = "Mono Heap (MB)";
            s_strings["pd_gc_gen0"] = "GC Gen0";
            s_strings["pd_sec_throughput"] = "⚡ Nexus Throughput";
            s_strings["pd_signals_per_s"] = "Signals/s";
            s_strings["pd_commands_per_s"] = "Commands/s";
            s_strings["pd_metrics_note"] = "Metrics are collected via the Nexus runtime. In the Editor, Mono Heap includes the editor's own managed memory (500MB-1GB is normal), so the memory chart and alarm measure growth since recording started (Δ). Exact values come from builds.";
            s_strings["pd_sec_alarms"] = "🔔 Alarm Thresholds";
            s_strings["pd_fps_alarm"] = "FPS Alarm (<)";
            s_strings["pd_mem_alarm"] = "Memory Alarm (Δ MB >)";
            s_strings["pd_sec_summary"] = "📋 Summary (Last Sample)";
            s_strings["pd_fps_below"] = "⚠ FPS BELOW THRESHOLD ({0} < {1})";
            s_strings["pd_mem_above"] = "⚠ MEMORY GREW ABOVE THRESHOLD (Δ {0} MB > {1} MB)";
            s_strings["pd_fps_current"] = "FPS (current)";
            s_strings["pd_fps_avg"] = "FPS (avg 60s)";
            s_strings["pd_fps_min"] = "FPS (min 60s)";
            s_strings["pd_mono_heap_short"] = "Mono Heap";
            s_strings["pd_mem_delta"] = "Δ since start";
            s_strings["pd_signals_current"] = "Signals/s (current)";
            s_strings["pd_commands_current"] = "Commands/s (current)";
            s_strings["pd_gc_delta"] = "GC Gen0 (delta)";

            // ── Cross-plugin navigation & stray labels ─────────
            s_strings["tr_pause"] = "Pause";
            s_strings["gm_no_active_contexts"] = "No active contexts.";

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
            s_strings["tr_detail_children"] = "\n<b>Children ({0}):</b>";
            s_strings["tr_detail_child_row"] = "  #{0} [{1}] {2} — {3}";
            s_strings["tr_tree_prefix"] = "└─ ";

            // ContextInspector
            s_strings["ci_null_label"] = "(null)";
            s_strings["hier_null_value"] = "null";

            // Status tab (#193)
            s_strings["status_problems"] = "Problems";
            s_strings["status_check"] = "Check";
            s_strings["status_check_tip"] = "Runs every check: validation, code health, the binder, package versions, run-time errors and, in Play Mode, the bindings of each running context.";
            s_strings["status_copy"] = "Copy report";
            s_strings["status_not_checked"] = "Not checked since the last compile. Press Check.";
            s_strings["status_summary"] = "{0} error(s), {1} warning(s) · checked at {2:HH:mm:ss}";
            s_strings["status_none"] = "No problems found.";
            s_strings["status_packages"] = "Packages";
            s_strings["status_unity"] = "Unity {0}";
            s_strings["status_tip_contexts"] = "Open the running contexts";
            s_strings["status_tip_handlers"] = "Open the signal handlers in the Architecture tab";
            s_strings["status_source_failed"] = "The check could not run ({0}: {1}), so it checked nothing.";
            s_strings["status_src_validation"] = "Validation";
            s_strings["status_src_code_health"] = "Code health";
            s_strings["status_src_binder"] = "Binder";
            s_strings["status_src_package_versions"] = "Package versions";
            s_strings["status_src_runtime"] = "Run time";
            s_strings["status_src_di"] = "Bindings";
            s_strings["status_fix"] = "Fix";
            s_strings["status_fix_open"] = "Open";
            s_strings["status_fix_generate"] = "Generate";
            s_strings["status_fix_package_manager"] = "Package Manager";
            s_strings["status_fix_errors"] = "Show errors";
            s_strings["status_fix_inspect"] = "Inspect";
            s_strings["status_binder_stale"] = "The generated binder or link.xml no longer matches the code. A player build regenerates both; Generate does it now.";
            s_strings["status_binder_stale_build_stops"] = "The generated binder or link.xml no longer matches the code, and NEXUS_DISABLE_AUTOGEN=1 makes a player build stop on it. Generate regenerates both.";
            s_strings["status_binder_dropped_injectors"] = "The container set aside generated injectors written for other members and injects these types by reflection: {0}. Generate the binder again.";
            s_strings["status_runtime_errors"] = "{0} error(s) recorded at run time. The newest: {1}";
            s_strings["status_runtime_warnings"] = "{0} warning(s) recorded at run time. The newest: {1}";


            s_strings["wizard_title"] = "CREATE A CONTEXT AND ITS SCRIPTS";
            s_strings["wizard_context_desc"] = "Creates <scope tag>ContextData.asset in the settings folder and a [<scope tag>_Root] in the open scene that starts with it.";
            s_strings["wizard_context_scopes"] = "Assemblies scanned for [SignalHandler] commands";
            s_strings["wizard_context_no_assemblies"] = "The project has no script under Assets yet, so the context scans no assembly. Add the assembly to the ContextData later.";
            s_strings["wizard_context_created"] = "Created {0} with {1}.";
            s_strings["wizard_parent_none"] = "None";
            s_strings["wizard_folder_outside_assets"] = "The folder must be under Assets.";
            s_strings["wizard_validation_type_name"] = "'{0}' is not a valid C# type name.";
            s_strings["wizard_validation_same_name"] = "The signal and the command need different names.";
            s_strings["wizard_files_exist"] = "Nothing was written, because this file already exists:\n{0}";
            s_strings["wizard_files_written"] = "Written:\n{0}";
            s_strings["wizard_next_bind"] = "Next: add this line to your lifecycle's OnConfigure:\n{0}";
            s_strings["wizard_next_view"] = "Next: add {0} to the GameObject that holds its UI, in a scene with a Root. {0}Mediator binds when the view enables.";
            s_strings["wizard_create_root_caps"] = "CREATE ROOT";
            s_strings["wizard_service_gen_desc"] = "Generates an INexusService interface + NexusService<T> implementation.";
            s_strings["wizard_view_gen_desc"] = "Generates a View + Mediator pair for the given view name.";
            s_strings["wizard_signal_gen_desc"] = "Generates a Signal struct + ICommand<T> implementation pair.";
            s_strings["wizard_generate_service"] = "GENERATE SERVICE";
            s_strings["wizard_generate_view"] = "GENERATE VIEW/MEDIATOR";
            s_strings["wizard_generate_signal"] = "GENERATE SIGNAL/COMMAND";
            s_strings["wizard_subtab_create_root"] = "Create Root";
            s_strings["wizard_subtab_service_gen"] = "Service Gen";
            s_strings["wizard_subtab_view_gen"] = "View/Mediator Gen";
            s_strings["wizard_subtab_signal_gen"] = "Signal/Cmd Gen";
            s_strings["wizard_field_scope_tag"] = "Scope Tag";
            s_strings["wizard_field_scripts_folder"] = "Scripts Folder";
            s_strings["wizard_field_settings_folder"] = "Settings Folder";
            s_strings["wizard_field_parent_root"] = "Parent Root";
            s_strings["wizard_field_view_name"] = "View Name";
            s_strings["wizard_field_signal_name"] = "Signal Name";
            s_strings["wizard_field_command_name"] = "Command Name";
            s_strings["wizard_field_service_name"] = "Service Name";
            // R2026-H10 fix: the View/Mediator wizard tab referenced this key but it was
            // never registered — the toggle rendered with its raw key as the label.

            s_strings["hierarchy_no_fields"] = "No fields or properties available.";
            s_strings["hierarchy_fields"] = "Fields";
            s_strings["hierarchy_properties"] = "Properties";
            s_strings["hierarchy_roots_detected"] = "SCENE ROOTS DETECTED ({0})";
            s_strings["hierarchy_roots_desc"] = "Found {0} Root GameObject(s) in the scene. These will initialize Contexts in Play Mode.";
            s_strings["hierarchy_handlers_pill"] = "{0} Handlers";
            s_strings["hierarchy_config_so"] = "Config SO";


            s_strings["tracer_title"] = "LIVE SIGNAL & COMMAND TRACER";
            s_strings["tracer_enable"] = "Enable Full Causal Tracing & Recompile";
            s_strings["tracer_type_filter"] = " Type:";
            s_strings["tracer_status_filter"] = " Status:";
            s_strings["tracer_debug_disabled_title"] = "CAUSAL TRACING: NEXUS_DEBUG DISABLED";
            s_strings["tracer_debug_disabled_desc"] = "Causal tracing is compiled out, so this tab records no events. Add NEXUS_DEBUG to record signals, commands and model changes with their parent/child chains.";


            s_strings["gamemanager_open_wizard"] = "Open Context Wizard";


        }

        [System.Serializable]
        private class LocaleData
        {
            public LocaleEntry[] entries = null;
        }

        [System.Serializable]
        private class LocaleEntry
        {
            public string key = null;
            public string value = null;
        }
    }
}
