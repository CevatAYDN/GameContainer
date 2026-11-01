> **For AI Agents:** This document is the source of truth for all localization key namespaces, keys, and translation parity rules in the Nexus framework. Before adding or editing UI strings:
> 1. Check existing namespaces in the table below to avoid key collisions.
> 2. Add keys to C# `NexusLang.AddDefaults()` (English) AND `Editor/Locales/tr.json` (Turkish).
> 3. Verify string formatting uses `{0}` placeholders (never C# string interpolation for words).
>
> **Supported Locales:** `en` (English), `tr` (Turkish).

# Nexus Localization Keys Reference

The keys here are the editor's. A game's own text goes through `LocalizationService` and its tables (package README, "Services").

This document lists the localization key namespaces used by the editor window (`NexusWindow`) and the 9 Nexus Editor Plugins in `com.nexus.core` (the `NexusEditorPlugin` subclasses under `Editor/Plugins/`; the State Machine tab moved to `com.nexus.fsm` in #112) to prevent key collisions and ensure translation parity between **English (`en`)** and **Turkish (`tr`)**.

**Parity:** every one of the 401 keys in `NexusLang.AddDefaults()` has an entry in `Editor/Locales/tr.json`, and `tr.json` has no key the code does not define. A new key goes into both files in the same change, and a changed English text gets its Turkish text in the same change.

A few `tr.json` entries deliberately keep the English text:

- `cs_default_currency` (`Coins`): it is the default currency ID the Casual Debugger sends to the economy service, not a label.
- Symbols and units (`pd_value_placeholder`, `tr_tree_prefix`, `pd_unit_mb`, `arch_tag_async`), `brand_title` and the `null` labels.

Code never compares a displayed text with an English literal: `ArchitecturePlugin` treats the first entry of its kind and assembly dropdowns by position, and `SignalFireForm` checks the preset list itself, so their labels are translated like any other.

---

## 🔑 Key Namespaces

| Prefix | Component / Plugin | Description |
|:---|:---|:---|
| `brand_`, `sidebar_`, `error_`, `actions_`, `status_edit_mode`, `status_play_mode_active`, `statusbar_` | NexusWindow | Window shell: brand, sidebar, plugin discovery errors, action bar, status bar |
| `cat_` | NexusWindow sidebar | Section headers: `cat_status`, `cat_setup`, `cat_architecture` and `cat_live`, in that order (`NexusWindow.Sections`); `INexusEditorPlugin.Category` returns one of them, and any other key, such as the default `cat_other`, is a section after them |
| `tab_` | NexusWindow sidebar | Sidebar button label per plugin, `tab_<plugin id in lower case>`; without the key the plugin's `DisplayName` is used. Also the `DisplayName` of the Status, Context Inspector, Error and Performance plugins |
| `action_` | Plugins | `action_<tool>_title`: the `DisplayName` of the Architecture, Casual Services, FSM, Tracer and Wizard plugins |
| no namespace prefix (`contexts`, `handlers`, `roots`, `live_hint`, `no_roots`, `ready`, ...), `system_` | StatusPlugin, shared labels | The Status tab's state card and labels other tabs share |
| `status_` (other than the window shell's `status_edit_mode` and `status_play_mode_active`) | StatusPlugin, `Validation/NexusStatus.cs`, `Validation/StatusSources.cs`, `com.nexus.ads`'s `AdsStatusSource` | The problems list, its checks (`status_src_`), fix buttons (`status_fix_`), messages and the packages card |
| `setup_` | NexusSetupWizard | The Setup section's start page: starter scene, package samples, the way into the Context Wizard, document buttons, version line |
| `wizard_` | WizardPlugin (`Wizard/WizardTabs.cs`, `Wizard/ScriptGenerator.cs`) | Context creation and the service, view/mediator and signal/command generators: fields, validation, results and next steps |
| `ci_`, `hierarchy_`, `hier_`, `gm_no_active_contexts` | ContextInspectorPlugin | The running contexts' tree; the selected context's overview, singletons with their fields, DI bindings, services, signal handlers, extensions, test fire |
| `arch_` | ArchitecturePlugin (`Plugins/Architecture/`) | The map of the game's signals, commands, services, models, views and mediators: filters, details, handlers, dependencies, graph (`arch_kind_<kind>` is built from `ArchitectureMap.Kind`) |
| `fire_` | SignalFireForm (`Plugins/Architecture/`) | The signal fire form the Architecture and Context Inspector tabs share: fields, context, result, presets |
| `tracer_`, `tr_` | TracerPlugin | Causal trace log, filters, event details (`tr_` is the tracer's prefix, not the Turkish locale) |
| `fsm_` | FSMPlugin (`com.nexus.fsm`) | Finite state machine visualizer |
| `ed_` | ErrorDashboardPlugin | Error log: severity and category filters, capture, copy and CSV export |
| `pd_` | PerformanceDashboardPlugin | FPS, Mono heap, GC Gen0, signal and command rates, alarms |
| `cs_` | CasualServicesPlugin | Economy, progression, UI stack, haptics and TimeScale debug helper |

---

## 📜 Active Key Catalog (Sample Mapping)

The complete lists are `NexusLang.AddDefaults()` (English) and `Editor/Locales/tr.json` (Turkish).

| Key | English (`en`) | Turkish (`tr`) |
|:---|:---|:---|
| `tab_status` | Status | Durum |
| `system_active` | SYSTEM ACTIVE | SİSTEM AKTİF |
| `system_standby` | SYSTEM STANDBY | SİSTEM BEKLEMEDE |
| `tracer_sig` | SIG | SİNYAL |
| `tracer_cmd` | CMD | KOMUT |
| `tracer_mod` | MOD | MODEL |
| `action_wizard_title` | Context Wizard | Context Sihirbazı |
| `ci_action_copy_report` | 📋 Copy Report | 📋 Raporu Kopyala |

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

**Re-review trigger:** Adding or modifying any localization key.
