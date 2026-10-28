> **For AI Agents:** This document is the source of truth for all localization key namespaces, keys, and translation parity rules in the Nexus framework. Before adding or editing UI strings:
> 1. Check existing namespaces in the table below to avoid key collisions.
> 2. Add keys to C# `NexusLang.AddDefaults()` (English) AND `Editor/Locales/tr.json` (Turkish).
> 3. Verify string formatting uses `{0}` placeholders (never C# string interpolation for words).
>
> **Supported Locales:** `en` (English), `tr` (Turkish).

# Nexus Localization Keys Reference

The keys here are the editor's. A game's own text goes through `LocalizationService` and its tables (package README, "Services").

This document lists the localization key namespaces used by the editor window (`NexusWindow`) and the 16 Nexus Editor Plugins (the `NexusEditorPlugin` subclasses in `Editor/Plugins/`) to prevent key collisions and ensure translation parity between **English (`en`)** and **Turkish (`tr`)**.

**Parity:** every one of the 686 keys in `NexusLang.AddDefaults()` has an entry in `Editor/Locales/tr.json`, and `tr.json` has no key the code does not define. A new key goes into both files in the same change, and a changed English text gets its Turkish text in the same change.

A few `tr.json` entries deliberately keep the English text:

- `cs_default_currency` (`Coins`): it is the default currency ID the Casual Debugger sends to the economy service, not a label.
- Symbols and units (`pd_value_placeholder`, `graph_port_output`, `graph_port_input`, `hier_bullet`, `tr_tree_prefix`, `exp_btn_copy`, `exp_btn_open`, `pd_unit_mb`, `nd_latency_default`, `nd_latency_range`), `brand_title` and the `null` labels.

Code never compares a displayed text with an English literal: `NetworkDashboardPlugin` keeps the event type behind each filter button and `ExplorerPlugin` treats the assembly dropdown's first entry and an empty preset list by position, so their labels are translated like any other.

---

## 🔑 Key Namespaces

| Prefix | Component / Plugin | Description |
|:---|:---|:---|
| `window_`, `brand_`, `sidebar_`, `discovery_`, `error_`, `actions_`, `status_`, `statusbar_` | NexusWindow | Window shell: brand, sidebar, plugin discovery errors, action bar, status bar |
| `cat_` | NexusWindow sidebar | Category headers; `INexusEditorPlugin.Category` returns one of these keys (default `cat_other`) |
| `tab_` | NexusWindow sidebar | Sidebar button label per plugin, `tab_<plugin id in lower case>`; without the key the plugin's `DisplayName` is used. Also the `DisplayName` of the Dashboard, Context Inspector, Error, Performance and Network plugins |
| `action_` | DashboardPlugin, other plugins | `action_<tool>_title` / `action_<tool>_desc`: the Dashboard's ALL TOOLS cards; the `_title` keys are also the `DisplayName` of the Casual Services, Explorer, FSM, Game Manager, Graph, Help, Hierarchy, Tracer, Type Analyzer and Wizard plugins |
| `nav_` | GameManagerPlugin, HierarchyPlugin | Buttons that open another plugin |
| no namespace prefix (`dashboard`, `contexts`, `build_validation`, `clear`, ...), `system_`, `perf_` | DashboardPlugin (`Dashboard/DashboardSections.cs`) | System status, project overview, build validation, runtime metrics, framework card |
| `dash_`, `db_` | DashboardPlugin (`Dashboard/DashboardSections.cs`) | Quick actions, QuickFind, card tooltips, Nexus Health and Build readiness cards |
| `setup_` | NexusSetupWizard | Localized setup steps, descriptions, actions and integration guide |
| `wizard_` | WizardPlugin (`Wizard/WizardTabs.cs`) | Bootstrap manifest, Root and code generation, clean deletion, dead code scan |
| `ci_` | ContextInspectorPlugin | Live context overview, DI bindings, singletons, services, signal handlers, extensions, test fire |
| `exp_`, `explorer_`, `ex_`, `common_` | ExplorerPlugin | Signal wiring, handlers, play-mode tester, presets |
| `hierarchy_`, `hier_` | HierarchyPlugin | Active context hierarchy tree and DI container inspector |
| `graph_` | GraphPlugin | Architecture graph visualization |
| `tracer_`, `tr_` | TracerPlugin | Causal trace log, filters, event details (`tr_` is the tracer's prefix, not the Turkish locale) |
| `gm_`, `gamemanager_` | GameManagerPlugin | Models, signals, commands, views, services, live rates, signal test panel |
| `fsm_` | FSMPlugin (`com.nexus.fsm`) | Finite state machine visualizer (GameManagerPlugin also uses `fsm_fallback_context`) |
| `ed_` | ErrorDashboardPlugin | Error log: severity and category filters, capture, copy and CSV export |
| `pd_` | PerformanceDashboardPlugin | FPS, Mono heap, GC Gen0, signal and command rates, alarms |
| `nd_` | NetworkDashboardPlugin | Connection status, latency, network event log |
| `cs_` | CasualServicesPlugin | Economy, progression, UI stack, haptics and TimeScale debug helper |
| `typeanalyzer_`, `ta_` | TypeAnalyzerPlugin | Type coupling and `[Inject]` dependency inspector |
| `help_` | HelpPlugin | Documentation & framework guide |

---

## 📜 Active Key Catalog (Sample Mapping)

The complete lists are `NexusLang.AddDefaults()` (English) and `Editor/Locales/tr.json` (Turkish).

| Key | English (`en`) | Turkish (`tr`) |
|:---|:---|:---|
| `dashboard` | Dashboard | Kontrol Paneli |
| `system_active` | SYSTEM ACTIVE | SİSTEM AKTİF |
| `system_standby` | SYSTEM STANDBY | SİSTEM BEKLEMEDE |
| `tracer_sig` | SIG | SİNYAL |
| `tracer_cmd` | CMD | KOMUT |
| `tracer_mod` | MOD | MODEL |
| `gm_quick_find` | Quick Find | Hızlı Bul |
| `gm_refresh_all` | Refresh All | Tümünü Yenile |
| `action_wizard_title` | Context Wizard | Context Sihirbazı |
| `ci_action_refresh` | 🔄 Refresh | 🔄 Yenile |

---

## 🌐 Supported Locales & Fallback Behavior

- **Locales:** `en` (English - Default) and `tr` (Turkish). The EN / TR buttons in the Nexus window store the choice in the `Nexus_Locale` EditorPrefs key; `NexusLang.LoadLocale` treats any other value as `en`.
- **Loading:** `LoadLocale` first fills the English defaults from `AddDefaults()`, then overlays `Packages/com.nexus.core/Editor/Locales/<locale>.json` loaded through `AssetDatabase`, which resolves the path for embedded, Git and tarball installs (`File.Exists` on a `Packages/` path does not). There is no `en.json`. A locale file that fails to parse logs one warning, `[NexusLang] Failed to load locale '<locale>'`, and the English defaults stay.
- **Fallback Rule:** If `NexusLang.Get("key")` fails to find the key in `s_strings`, it returns `"key"` directly, silently: no warning is logged in any build. A key missing from `tr.json` therefore shows its English default, and a key missing from `AddDefaults()` as well shows the raw key. A caller can detect the miss by comparing the result with the key, as `NexusWindow` does for `tab_` labels.
- **Integration guide:** the setup wizard opens `docs/INTEGRATION_PATHS_TR.md` when the locale is `tr` and `docs/INTEGRATION_PATHS.md` otherwise, from the package's `PackageInfo.resolvedPath`.

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Framework index and decision flows
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development guidelines

---

**Code version:** 0.5.0
**Maintainers:** Nexus Core Team
**Re-review trigger:** Adding or modifying any localization key.
