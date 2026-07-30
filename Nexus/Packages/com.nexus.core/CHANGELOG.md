# Changelog

All notable changes to the Nexus Core package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- `NexusRuntime.Reset()` deadlock fix using snapshot-then-dispose pattern to dispose contexts outside locks during Play Mode transitions.
- Pure context lifecycle duplication — `CreatePureContextAsync` now routes through `Context.InitializeLifecycleAsync`.
- `SignalBus.BroadcastCrossContext` global registry access now abstracts cleanly via `IContextResolver`.
- Plugin discovery failures (`ReflectionTypeLoadException`) now render diagnostic details directly in the `NexusWindow` UI.
- `TracerPlugin` live trace state leaks — `_hasLiveEvents` and queues now reset cleanly in `OnDisable`.
- `DashboardPlugin` action card double-click bug — removed duplicate `MouseDownEvent` callback.
- `DashboardPlugin.QuickFind` keystroke lag — implemented static `s_typedCatalog` scanning cache with 200ms input debounce.
- `GameManagerPlugin.FireTestSignal` reflection overhead — added `s_fireMethodCache` for `MethodInfo` caching.
- `TracerPlugin` CS0411 generic callback type inference compile error fixed by specifying `EventCallback<MouseDownEvent>` explicitly.

### Changed
- Refactored 5 critical editor plugins (`Dashboard`, `Tracer`, `GameManager`, `Wizard`, `ContextInspector`) to override `OnUpdate` instead of creating custom UI timers (`_view.schedule` / `_root.schedule`).
- Standardized `OnDisable` state cleanup across all editor plugins.
- Replaced empty `catch {}` reflection blocks with `ReflectionTypeLoadException` warning logs.
- Consolidated duplicate stat card implementations into `NexusEditorStyles.CreateStatTile`.
- `TracerPlugin` log list converted from `ScrollView` rebuild to virtualized `ListView` with `makeItem`/`bindItem` pooling.
- Fixed `TracerPlugin` `ListView.fixedItemHeight` to `28f` for safe label padding.

### Added
- `IContextResolver` interface for safe cross-context signal dispatch.
- `NexusEditorStyles.CreateStatTile` helper method for unified metric card rendering.
- `NexusVisualization` — extracted sparkline, gauge, data-table, and stat-row helpers from `NexusEditorStyles`.
- `DashboardSections` — reusable UI-building helpers extracted from `DashboardPlugin`.
- `WizardTabs` — `IWizardTab` interface + 5 tab implementations extracted from `WizardPlugin`.
- Form validation (empty-field check + disabled Generate button) in `CreateRootTab`, `ViewMediatorGenTab`, and `SignalCommandGenTab`.
- `PluginRefactorValidationTests` under `Tests/Editor/` validating plugin lifecycles, virtualized item heights, and scheduler rules.
- `Editor/Locales/en.json` and `tr.json` localization catalogs.

### Changed
- `NexusDI` — added `EditorResolvedSingletons`, `GetEditorSingletonSnapshot()`, `GetEditorTypeMappings()` safe accessors for editor tools (replaces fragile reflection).
- `Context` — exposed `Builder` accessor property for editor inspection.
- `NexusEditorDataProvider` — replaced 4 `GetField`/`GetProperty` reflection calls with direct accessor calls.
- `ExplorerPlugin` — replaced `GetField("_bindings")` reflection with `GetEditorSingletonSnapshot()` call.
- `WizardPlugin` — decomposed monolithic ~1025-line class into coordinator (295 lines) + 5 `IWizardTab` implementations in `WizardTabs.cs`.
- Empty `catch {}` blocks in `NexusEditorDataProvider` and `ExplorerPlugin` now log via `Debug.LogWarning` instead of swallowing silently.

### Removed
- `NexusEditorStyles` — 5 visualization methods deprecated (`[Obsolete]` pointing to `NexusVisualization`).
- `DashboardSections.BuildStatusSection()` and `BuildHealthSection()` (unused — `DashboardPlugin` keeps inline versions).
- `WizardPlugin` — ~730 lines of dead `Build*Tab()` methods and ~90 lines of dead action/validation helpers (migrated to `WizardTabs.cs`).
- `WizardPlugin.FindBootstrapManifest()` — became unreachable after dead code removal.

### Removed
- Unused language support for `ja`, `zh`, and `ko` from `NexusLang.cs` to focus exclusively on English (`en`) and Turkish (`tr`).
- Dead code `_renderedItems` collection in `TracerPlugin.cs`.
- Custom `_view.schedule` and `_root.schedule` timer calls in all editor plugins.

### Deprecated
- `_view.schedule` / `_root.schedule` pattern in editor plugins — plugins must override `INexusEditorPlugin.OnUpdate()` instead.

## [0.3.2] - 2026-06-15

### Added
- Causal tracing sink (`INexusTraceSink`) for background signal logging.
- `BuildValidation` silent rules engine for CI pipeline verification.
- `ObservableProperty<T>` for zero-GC reactive model updates.
