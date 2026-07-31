# Changelog

All notable changes to the Nexus Core package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Breaking
- `IEconomyService.GetObservableBalance` return type changed from `ObservableProperty<long>` to `SecureObservableLong` (the new XOR-masked anti-cheat wrapper). The `Value`/`OnChanged`/`SetWithoutNotify` surface is identical, but code that typed the result as `ObservableProperty<long>` must be updated.

### Security
- Added `SecureObservableLong` (XOR-masked RAM obfuscation, mirroring `SecureObservableInt`) and migrated `EconomyService` balances to it — currency values are no longer stored as plain `long` in memory, closing the anti-cheat gap flagged for RAM scanners (GameGuardian/CheatEngine).
- `EconomyService.Earn` now clamps at `long.MaxValue` instead of overflowing to a negative balance.
- `EconomyService.Spend` is now reconciled with the network validator: a server-rejected spend rolls the locally-deducted amount back (bounded at `long.MaxValue`), preventing client/server balance desync.

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

### Fixed (Runtime adversarial review)
- PerformanceMonitor `s_samples` queue is now bounded (2000 samples) and history is a FIFO `Queue<float>` — previously the sample queue grew forever between `ClearHistory` drains, leaking managed heap (observed climbing to ~800 MB).
- `PerformanceMonitor.UpdateFrameMetrics` throttled to ~10 Hz (6-frame cadence) — per-frame sampling created ~180 allocations/sec of GC churn that spiked FPS every few seconds.
- `PerformanceMonitor` history is a bounded FIFO `Queue<float>` (O(1) dequeue, capped at `MaxHistorySize`) that never leaks regardless of recording state; only the sample queue and event notifications are gated on recording.
- `NexusDI` field/property injection and clearing now use compiled Expression-tree setters with a reflection fallback (AOT/IL2CPP-safe); the repeated setter-or-reflection branches were consolidated into shared helpers.
- `NexusDI` setter compile failures are logged once per type (was a silent `catch {}`) instead of being swallowed.
- `NexusVisualization.UpdateSparkline` reuses its bar children across refreshes instead of `Clear()`+re-adding hundreds of `VisualElement`s every 0.5 s while playing.
- `PerformanceDashboardPlugin` stat-row cache is cleared when the view/container is rebuilt or the plugin is disabled, preventing stale detached rows from leaving the summary card empty after a window reopen.
- Generic-only recovery fallback commands (`ICommand<TSignal>` / `IAsyncCommand<TSignal>` without the non-generic interface) were silently skipped by the object-based dispatch paths. They now execute via cached reflection dispatchers.
- Sync recovery error handler no longer dispatches async-only fallback types (which would throw and re-enter the same strategy decision, recursing forever) — they are rejected and treated as `Skip`.
- `RegisterCompositeCommand` now rejects duplicate and null signal types up front (a duplicate would set the same mask bit twice and the trigger could never fire).
- `NexusService<T>` double-dispose — `NexusDI.Dispose`/`DisposeAsync` now skip `INexusService` singletons (their lifecycle is owned by the Context, which calls `OnDispose()` exactly once, including lazy services resolved outside the eager `ServiceTypes` list).
- Lazy services first resolved during `OnStartAsync` now still receive `InitializeAsync` (second lazy drain after `OnStartAsync`).
- `CommandPool` double-return guard — an instance already in the pool is discarded instead of being pooled twice.
- `ContextBuilder.Validate()` now validates concrete implementations (`Bind<TInterface, TImplementation>`), previously only interface keys were checked, and no longer flags `LazyInjection<T>` fields (constructed directly by the injector).
- `SignalBus` handler/subscription read-copies are `volatile` with deep-copied snapshots; `FireAsyncAndForget` no longer surfaces `OperationCanceledException` during teardown; dead subscription nodes are swept immediately when not dispatching.
- `WindowManager.IsWindowOpen`/`GetWindow` now use a bounded 50 ms wait instead of `Wait(0)` — a briefly-held semaphore (background async open/close) no longer yields a false "window closed" that could trigger duplicate opens.
- `ObjectPoolService.DespawnAfter` now guards timers with a per-instance spawn-session generation — an instance manually despawned and re-spawned before the timer fires is no longer yanked out of the scene by the stale timer.
- `EncryptedStorageService.SaveKeyToDisk` retries the delete+move swap up to 3 times on `IOException` (Windows file-handle propagation: antivirus/indexer), instead of failing a save when the destination handle lingers.
- `CommandPoolManager.GetCommand` no longer allocates a closure on every call — the pool factory delegate is cached once per manager (hot-path allocation-free; also avoids the `GetOrAdd<TArg>` overload that is unavailable on .NET Standard 2.0).

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

### Changed (Runtime)
- `PerformanceMonitor` built-in metric updates (`UpdateFrameMetrics`/`UpdateMemoryMetrics`/`UpdateGCMetrics`) now early-out when not recording, and the initial frame metric is no longer silently dropped by the throttle guard.
- `PerformanceDashboardPlugin.OnDisable` now resets `_recording`, the stat-row cache, and the stats container reference (CONTRIBUTING state-reset rule).

### Changed (Runtime)
- `NexusDI` — added `EditorResolvedSingletons`, `GetEditorSingletonSnapshot()`, `GetEditorTypeMappings()` safe accessors for editor tools (replaces fragile reflection).
- `Context` — exposed `Builder` accessor property for editor inspection; `InitializeReactiveModelsAsync` dropped its unused `signalBus` parameter.
- `NexusEditorDataProvider` — replaced 4 `GetField`/`GetProperty` reflection calls with direct accessor calls.
- `ExplorerPlugin` — replaced `GetField("_bindings")` reflection with `GetEditorSingletonSnapshot()` call.
- `WizardPlugin` — decomposed monolithic ~1025-line class into coordinator (295 lines) + 5 `IWizardTab` implementations in `WizardTabs.cs`.
- Empty `catch {}` blocks in `NexusEditorDataProvider` and `ExplorerPlugin` now log via `Debug.LogWarning` instead of swallowing silently.

### Removed
- `NexusEditorStyles` — 5 visualization methods deprecated (`[Obsolete]` pointing to `NexusVisualization`).
- `DashboardSections.BuildStatusSection()` and `BuildHealthSection()` (unused — `DashboardPlugin` keeps inline versions).
- `WizardPlugin` — ~730 lines of dead `Build*Tab()` methods and ~90 lines of dead action/validation helpers (migrated to `WizardTabs.cs`).
- `WizardPlugin.FindBootstrapManifest()` — became unreachable after dead code removal.
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
