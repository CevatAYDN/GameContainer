> **For AI Agents:** This document is the source of truth for all localization key namespaces, keys, and translation parity rules in the Nexus framework. Before adding or editing UI strings:
> 1. Check existing namespaces in the table below to avoid key collisions.
> 2. Add keys to C# `NexusLang.AddDefaults()` (English) AND `Editor/Locales/tr.json` (Turkish).
> 3. Verify string formatting uses `{0}` placeholders (never C# string interpolation for words).
>
> **Supported Locales:** `en` (English), `tr` (Turkish).

# Nexus Localization Keys Reference

This document lists all active localization key namespaces and keys used across the 15 Nexus Editor Plugins to prevent key collisions and ensure translation parity between **English (`en`)** and **Turkish (`tr`)**.

---

## 🔑 Key Namespaces

| Prefix | Component / Plugin | Description |
|:---|:---|:---|
| `dashboard` / `dash_` | DashboardPlugin | System status, QuickFind, framework overview |
| `tracer_` | TracerPlugin | Causal trace log, filters, event details |
| `gm_` / `gamemanager_` | GameManagerPlugin | Models, signals, commands, views, services, live rates |
| `wizard_` | WizardPlugin | Code generator templates, context creation |
| `ci_` | ContextInspectorPlugin | Scene roots, context tree inspector |
| `exp_` | ExplorerPlugin | Signal wiring, handlers, presets |
| `hierarchy_` | HierarchyPlugin | Active context hierarchy tree |
| `graph_` | GraphPlugin | Architecture graph visualization |
| `fsm_` | FSMPlugin | Finite state machine visualizer |
| `err_` | ErrorDashboardPlugin | Build & runtime error log hub |
| `pd_` | PerformanceDashboardPlugin | FPS, frame timing, GC memory profiler |
| `nd_` | NetworkDashboardPlugin | Multi-player netcode stats |
| `cs_` | CasualServicesPlugin | Economy, window & haptic debug helper |
| `typeanalyzer_` | TypeAnalyzerPlugin | Assembly & type reflection inspector |
| `help_` | HelpPlugin | Documentation & framework guide |

---

## 📜 Active Key Catalog (Sample Mapping)

| Key | English (`en`) | Turkish (`tr`) |
|:---|:---|:---|
| `dashboard` | Dashboard | Kontrol Paneli |
| `system_active` | SYSTEM ACTIVE | SİSTEM AKTİF |
| `system_standby` | SYSTEM STANDBY | SİSTEM BEKLEMEDE |
| `tracer_sig` | SIGNALS | SİNYALLER |
| `tracer_cmd` | COMMANDS | KOMUTLAR |
| `tracer_mod` | MODELS | MODELLER |
| `gm_quick_find` | Quick Find | Hızlı Arama |
| `gm_refresh_all` | Refresh All | Tümünü Yenile |
| `action_wizard_title` | Code Wizard | Kod Sihirbazı |
| `ci_action_refresh` | Refresh Tree | Ağacı Yenile |

---

## 🌐 Supported Locales & Fallback Behavior

- **Locales:** `en` (English - Default) and `tr` (Turkish).
- **Fallback Rule:** If `NexusLang.Get("key")` fails to find the key in `s_strings`, it returns `"key"` directly. In development builds, a console warning log is emitted.

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Framework index and decision flows
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development guidelines

---

**Last updated:** 2026-07-24  
**Code version:** 0.4.0  
**Maintainers:** Nexus Core Team  
**Re-review trigger:** Adding or modifying any localization key.
