# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Headless Code Health Analyzer gate (`NexusArchitectureAnalyzer.RunHeadless`)** — batch-mode entry point runs NEXUS001/002/003 analysis and exits 0 when clean / 1 when issues are found; wired into the CI Unity job. NEXUS002 (`async void`) extracted to an `IsNexus002Violation` predicate locked by editor tests, matching NEXUS001/NEXUS003. Full-project re-scan: 0 issues across 185 files (previous 15 resolved: 14× NEXUS004 WindowManager + 1× NEXUS001 SaveThrottler).

### Removed
- **`WindowManager` / `IWindowManager` deleted** — the legacy string-keyed window API is gone; `UIManager` (type-safe `ScreenView` API with pooling) is the single UI manager. `UILayer`/`IUIWindowLifecycle` extracted to their own files; demo screens migrated to `ScreenView`; the analyzer migration-driver rule NEXUS004 and its editor tests retired; benchmark W1–W7 migrated to UIManager U1–U7.
- **`Assets/Scripts/Demo/` scaffolding removed** — the Unity demo (`DemoGlobalLifecycle`, 4 `ScreenView` screens, demo commands/models/signals) was never wired to a scene or prefabs (no bootstrap in `NexusStarter.unity`, zero Resources prefabs). `Game/Samples` is the single canonical example: scene-wired, scaffolded by `NexusSetupWizard`, canonical in the quickstart/how-to docs. `DemoCompatibilitySuite` was removed with it; the wizard default view name and the `cs_default_window` localization key were cleaned up.

### Changed
- **Demo + tooling consolidated on `UIManager`** — demo screens derive from `ScreenView` and open via `IUIManager.OpenScreenAsync<TScreen>`; `CasualServicesPlugin` drives UIManager's new non-blocking `GetOpenScreensSnapshot`/`PendingScreenCount`; `CounterLifecycle` sample binds `IUIManager`. UIManager editor tests extended (`ScreenOpened`/`ScreenClosed` events, layer-root parenting).
- **Service-graph regression gate restored (`tools/nexus-benchmark/ServiceGraphSuite.cs`)** — successor to the deleted `DemoCompatibilitySuite`: boots the full package service graph (all runtime services + providers + adapter factories, no demo stand-ins) under `EnableStrictInjection` + `FailOnValidationErrors`. `SVC1` validates with zero DI issues, boots, resolves every service by interface and concrete type, shares ONE `SaveThrottler` between economy and progression, and proves the validator flags a missing provider.
- **Lifecycle-discovery docs aligned with the shipped sample** — `10_MIN_QUICKSTART` (en+TR) now shows `GameLifecycle : MonoBehaviour` attached to `GameRoot` (what the wizard generates) and documents both discovery paths (Root component scan + `{ScopeTag}Lifecycle` convention); `THE_BIG_NEXUS_HOWTO` wording corrected.

### Performance
- **`Binder.Unbind` O(n) scan (`Binder.cs`)**: added a secondary key index so `Unbind` removes a key's bindings directly instead of scanning the whole entry table under the exclusive write lock (which blocked every concurrent `Get`/`TryGet` reader). Reads are untouched; index entries are deduplicated on rebind.
- **`CommandExecutor` timeout path (`CommandExecutor.cs`)**: the timeout logic (linked `CancellationTokenSource` + `CancelAfter` timer, allocated per async dispatch) was duplicated inline in the object-path dispatcher; it is now consolidated into the single `ExecuteAsyncDispatcherWithOptionalTimeout` helper, and the linked CTS is skipped entirely when the parent token is already cancelled (teardown floods allocate nothing). Commands without `[CommandTimeout]` keep the zero-allocation direct path.
- **`NexusDebugHUD` log allocation (`NexusDebugHUD.cs`)**: `LogSignal`/`LogError`/`LogWarning` no longer build rich-text strings (interpolation + `ColorUtility.ToHtmlStringRGB`) while the HUD is hidden, the HTML color strings are cached once instead of per log line, and the `OnGUI` snapshot copy is version-gated so it only runs when new lines arrived.
- **`CommandPool` startup reflection (`CommandPool.cs`)**: the state-leak warning scan now claims its slot under the lock then scans OUTSIDE it (pool construction no longer serializes behind a global lock), uses allocation-free `IsDefined` instead of `GetCustomAttribute`, and the dead `s_injectableTypeCache`/`s_injectableCacheLock` fields were removed.
- **`ErrorCollection` LINQ removal (`ErrorCollection.cs`)**: `GetErrors`/`GetRecentErrors`/`GetFrequentErrors`/`GetSeverityCounts`/`GetErrorCounts`/`GetCategoryCounts`/`ClearBefore` replaced the `ToArray`+`OrderBy`+`Where`+`Take`+`GroupBy` LINQ chains with manual loops — the FIFO queue is walked backwards (newest-first), so the redundant full sort is gone and `Timestamp` is now assigned inside the write lock so queue order is exactly chronological. One snapshot array + one result array per call.
- **`BuildValidation` shared scan caches (`BuildValidation.cs`)**: the ten validation passes each re-scanned every loaded assembly and re-instantiated every attribute. Added shared caches (per-assembly types, per-type `[SignalHandler]`/`[CompositeSignalHandler]`/`[StubService]`/`[ContextDependsOn]` attributes, writeable-model verdicts), built `BuildTypeScriptCache`/`BuildSignalTypeMap` once per script reload instead of twice/once per run, cached file reads per run, converted the O(n²) `ContextData` `DependsOn` check to an O(n) scope dictionary, and replaced `GetCustomAttribute` with `IsDefined` everywhere.
- **`NexusCodeGenerator` reflection (`NexusCodeGenerator.cs`)**: `GetFields`/`GetProperties`/`GetMethods` now run once per type (shared `MemberSet` cache across the discovery/value-check/injector/clearer/preserve passes) and member scans use `IsDefined` instead of attribute instantiation.
- **Editor plugins (`NexusWindow.cs`, `ExplorerPlugin.cs`, `NexusEditorDataProvider.cs`, `ContextInspectorPlugin.cs`, `ErrorDashboardPlugin.cs`)**: `NexusWindow` caches the discovered plugin type list across window opens (re-scan only on script reload); `ExplorerPlugin`/`NexusEditorDataProvider` guard attribute scans with `IsDefined`; `ContextInspectorPlugin` caches per-type interface summaries and readable-property metadata; `ErrorDashboardPlugin` re-styles its severity filter bar in place instead of rebuilding it, throttles the error-list rebuild to actual changes, and filters search text with a manual loop.
- **`PerformanceDashboardPlugin` signal/command rates (`PerformanceDashboardPlugin.cs`)**: the rates previously counted `PerformanceMonitor.OnMetricRecorded` samples with category "Signal"/"Command" — nothing records those categories (MetricsSampler records Frame/Memory/GC only), so they always read 0.0, and the subscription added per-record delegate overhead. Rates now come from deltas of `NexusRuntime.Metrics`' Interlocked totals (exact, allocation-free), each ring buffer is copied once per tick and shared between the sparkline redraw and the stats summary, and LINQ `Average`/`Min` were replaced with a single manual pass.
- **`PerformanceDashboardPlugin` editor-heap false alarm (`PerformanceDashboardPlugin.cs`)**: the memory metric read `Profiler.GetMonoUsedSizeLong()` from the editor window, which includes the editor's own managed heap — the dashboard displayed ~800MB at startup and tripped the 512MB alarm instantly. It now prefers the runtime-recorded `MonoUsed` metric (sampled by `MetricsSampler` from the game loop; exact in builds), baselines the heap at recording start, and drives the sparkline + alarm + CSV from the session **delta** (absolute value stays in the big label). The note text and alarm labels explain the Δ semantics and that a build gives exact numbers.

### Fixed
- **SaveThrottler Lock Discipline + Silent Save-Loss Race (SaveThrottler.cs)** — `Tick()` now claims due slots under `_lock` and flushes user save actions OUTSIDE it: holding the lock across `action.Invoke()` serialized every save behind the slowest action and risked deadlock when an action blocked on a thread that wanted `_lock`. `FlushSlot` also queues a request that races an in-flight flush as pending instead of silently dropping it, so the newest state is never lost.
- **GameSaveManager Non-Async Lambda + Documented Backoff (GameSaveManager.cs)** — removed a pointless `async` on the `Task.Run` lambda (CS1998) and documented why the retry backoff stays inside the serialized stage+rename critical section (releasing `_saveLock` between attempts could clobber a concurrent same-slot save).
- **NEXUS003 Sync-Over-Async Detection (NexusArchitectureAnalyzer.cs)** — the analyzer now also flags `GetAwaiter().GetResult()` (not just `Thread.Sleep`) and honors a trailing `// NEXUS003-exempt: <reason>` marker for deliberate, documented sync sites (EncryptedStorageService 1-2 ms IO backoff, GameSaveManager retry backoff, NexusTestHarness rethrow-only GetResult).
- **GC Gen0 value never displayed (`PerformanceDashboardPlugin.cs` + `NexusLang.cs` + `tr.json`)**: the big GC label called `string.Format(NexusLang.Get("pd_gc_gen0"), ...)`, but `pd_gc_gen0` is the section title ("GC Gen0", no `{0}` placeholder) — it was defined twice in `NexusLang.cs` (a `"gen0 +{0:F0}"` value format that got overridden, then the title again) and once in `tr.json`, so the format silently dropped the argument and the label permanently read "GC Gen0". The title and the value format are now separate keys (`pd_gc_gen0` stays the title; new `pd_gc_gen0_value` = `"gen0 +{0:F0}"` added to defaults and `tr.json`), the GC summary row shows "—" when unsampled, and the label shows the per-tick Gen0 delta.
- **Performance tab never records if open before Play (`PerformanceDashboardPlugin.cs`)**: `CreateView` only auto-started recording when `Application.isPlaying` was already true at tab creation — entering Play Mode with the tab already open never re-ran `CreateView`, so `_recording` stayed false and the whole panel showed "—" forever. The plugin now subscribes to `EditorApplication.playModeStateChanged` (unsubscribe-before-subscribe in `CreateView`, unsubscribed in `OnDisable`, mirroring `DashboardPlugin`/`ExplorerPlugin`): `EnteredPlayMode` auto-starts with a fresh baseline, `ExitingPlayMode` stops.
- **Built-in metrics gated on recording (`PerformanceMonitor.cs`)**: `UpdateFrameMetrics`/`UpdateMemoryMetrics`/`UpdateGCMetrics` early-returned on `!s_recording`, so FPS/Memory/GC were only queryable via `GetMetric` while the dashboard was recording — a scene without the dashboard open (or before its `StartRecording` ran) showed flat 0.0/"—" even with a `MetricsSampler` present. Recording now only gates the sample queue + `OnMetricRecorded` events (which `RecordMetric` already does internally); the built-in metrics are always recorded while `Enabled`, so the Performance Dashboard reads live values the moment it opens. Harness proof E6 locks this in.
- **No FPS without a MetricsSampler (`PerformanceDashboardPlugin.cs`)**: the dashboard read only `PerformanceMonitor.GetMetric("FPS")`; scenes without a MetricsSampler showed "—" forever. It now falls back to `Time.deltaTime` (the game frame time in play mode; `dt >= 1s` hitches are skipped, not plotted as 1 FPS).
- **Programmatic Roots missing QueueDrainer + MetricsSampler (`Root.cs`)**: `Root` documented these as living on its GameObject but never added them — only the hand-edited starter scene carried them. Every programmatic creation path (Dashboard "Create Root", `GameObject → Nexus → Create Root`, Wizard scene scaffolding, `AddComponent<Root>()` in user code) produced a Root without them: the `HybridQueue` was NEVER drained (queued `FireThreadSafe`/`FireNextFrame` signals silently never ran) and the game never recorded FPS/memory/GC, so the Performance Dashboard read a flat 0.0. `Root.Awake` now auto-adds both when missing (`GetComponent` guard makes it idempotent); harness test L4 locks this in.
- **Error Dashboard summary vs. list mismatch (`ErrorDashboardPlugin.cs`)**: the severity pills counted ALL errors (`GetSeverityCounts`) while the list showed only the filtered subset, so the pills disagreed with the rows whenever a severity/category/search filter was active. Summary and list now derive from one `GetErrors` call (search included), so they can never disagree.
- **Error Dashboard duplicate `OnErrorAdded` subscriptions (`ErrorDashboardPlugin.cs`)**: `CreateView` subscribed unconditionally, and `NexusWindow` could re-invoke it (tab re-click, play-mode refresh) without an intervening `OnDisable` — each re-open leaked another subscription, so one error fired the handler N times. `CreateView` now unsubscribes before subscribing.
- **`NexusWindow` view churn on same-tab click (`NexusWindow.cs`)**: clicking the already-active tab called `RefreshActivePlugin()` — re-running `CreateView()` without `OnDisable()`, leaking plugin event subscriptions and rebuilding the whole UI on every click. The same-tab branch now only refreshes the action bar and status text; `UpdateStatusBarText` (200ms tick + play-mode/hierarchy/context callbacks) is wrapped in try/catch so a data-provider hiccup cannot spam the console every 200ms.
- **`Root.SetUp`/`RegisterLifecycle` silent no-op after Awake (`Root.cs`)**: both now throw `InvalidOperationException` when called after the context was already created — previously the configuration was silently dropped (the demo's boot path calls them before `SetActive(true)`, so it is unaffected).
- **`Context` double-Configure guard conflated harness builder (`Context.cs`)**: the guard keyed on `_builder != null`, which is also set by `GetOrCreateBuilder()` (the `NexusTestContext.Builder` harness path) — so binding through the harness and then calling `Configure()` silently skipped validation/scanning/lifecycle discovery. The guard now keys on a dedicated `_configured` flag; a pre-built harness builder is reused when present.
- **SaveThrottler Multi-Owner (`SaveThrottler.cs`)**: Replaced the single pending-slot design with per-owner slots (`TryRequestSave(owner, ...)` / `ForceSave(owner, ...)`). Previously, when two services (Economy + Progression) shared one throttler singleton, the last `TryRequestSave` silently dropped the other service's pending write — real data loss. Failure backoff/retry-cap is now isolated per owner, and `Flush()` persists every owner.
- **`[OptionalInject]` Never Injected (`NexusDI.cs`)**: `OptionalInjectAttribute` does not derive from `InjectAttribute`, so members decorated only with `[OptionalInject]` were absent from the injectable metadata and were never injected — even when a binding existed (e.g. `EconomyService.SaveThrottler` stayed null, silently disabling write-coalescing). Injectable and clearable metadata now accept both attributes; missing optionals are still skipped by validation and strict injection.
- **EncryptedStorageService DI Construction (`EncryptedStorageService.cs`)**: Added a parameterless constructor delegating to the default salt. The sole optional-string constructor made strict injection fail on the unresolved `System.String` parameter, breaking any container that bound the service.
- **Harness `ValidateOnStartup` leak (`FullArchitectureStressSuite.cs`)**: the stress suite opted out of startup DI validation for its own late-binding tests but never restored the static flag, so every later suite silently ran with validation disabled. `Run()` now restores the framework default in a `finally` block.
- **AOT Dashboard false warning (`NexusCodeGenerator.cs`)**: `HasInjectableTypes` scanned fields/properties but not method-level `[Inject]`, so a type injecting only via methods produced a false "AOT generation disabled" Dashboard warning.
- **AOT Binder Generator (`NexusCodeGenerator.cs`)**: mirrors the `[OptionalInject]` metadata fix for the AOT/IL2CPP path — optional-only members were previously excluded from the generated injectors (never injected even when bound) and `di.Resolve<T>()` was emitted for them (throwing at boot when unbound, e.g. `INetworkEconomyValidator`/`ILocalizationTableProvider` in the demo). Optional members/params now emit `di.TryResolve<T>()` (null when unbound). `NexusGeneratedBinder.g.cs` was hand-synced to the fixed output — **regenerate it in the Unity editor** (`Nexus → Generate AOT Binder`) so the AOT path matches.

### Added
- **`NexusArchitectureAnalyzerTests` (Tests/Editor/)** — 5 NUnit tests locking the NEXUS003 predicate (`IsNexus003Violation`): flags unmarked `GetAwaiter().GetResult()` and `Thread.Sleep` in runtime code, honors the `// NEXUS003-exempt:` marker, exempts Editor paths, and ignores benign `await` calls.
- **NEXUS003 exemption policy + ADR-0001 addendum** — CONTRIBUTING §4 documents when a blocking site may be exempted (`// NEXUS003-exempt: <reason>`; awaiting genuinely impossible) vs must become a real `await`; ADR-0001 gains a dated addendum recording the UIManager-forward / WindowManager-retained reality, closing the doc-vs-code contradiction.
- **Harness proof E6 (`EvidenceSuite.cs`)**: `PerformanceMonitor_BuiltinMetrics_WithoutRecording` — `UpdateFrameMetrics`/`UpdateMemoryMetrics`/`UpdateGCMetrics` populate `GetMetric` (FPS from `Time.deltaTime`, `TotalMemory` from `System.GC`) while recording is OFF, proving the dashboard no longer depends on its own recording state.
- **Harness test L4 (`LifecycleSuite.cs`)**: `Root_AutoAdds_QueueDrainer_And_MetricsSampler` — a programmatically created Root (via `AddComponent<Root>()` + `SetUp` + `Awake`) must end up with exactly one `QueueDrainer` and one `MetricsSampler`, and a second `Awake` must not double-add.
- **Harness tests 42–44 (`FullArchitectureStressSuite.cs`)**: `ContextData.DependsOn` PostContext ordering (chain invariant + cycle fallback + unknown dep), named `LazyInjection` resolving `[Inject(Name=...)]` bindings on field and property paths, and the `Root.SetUp`+`RegisterLifecycle` programmatic path incl. the new guards. Stress suite count is now 49.
- **DemoCompatibilitySuite (`tools/nexus-benchmark/DemoCompatibilitySuite.cs`)**: Replicates the Unity demo's binding graph in the harness and proves it validates with zero DI issues, boots under strict injection, resolves every demo service, shares one `SaveThrottler` singleton between economy and progression, and wires signal→command bindings.
- **Harness test 41 (`SaveThrottler_MultiOwner_NoCrossClobber`)**: deterministic cross-owner pending-slot, failure-isolation, flush-all, scoped `ForceSave`, and per-owner retry-cap regression test.
- **Harness AOT drift guard (`BinderSuite.cs`)**: conditional `AOT1. Binder_OptionalInject_TryResolveEmit` test — when `NexusGeneratedBinder.g.cs` exists it asserts the generated injectors use `TryResolve` for optional deps and inject `SaveThrottler` into Economy/Progression, failing on binder↔runtime drift.
- **AOT binder generator compile guard (`tools/nexus-benchmark`)**: the editor-only `NexusCodeGenerator.cs` is now compiled in the harness (with small UnityEditor stubs in `GeneratorStubs.cs`) so a codegen typo that would break the `Nexus.Editor` assembly inside Unity fails the harness build.

## [0.5.1] - 2026-08-03

### Fixed
- **Anti-Cheat Integrity Canary (`SecureObservableProperty.cs`)**: Fixed dead-code branch in tamper detection so canary failures log security warnings and reset tampered values to defaults.
- **Encrypted Storage Seed Exception Logging (`EncryptedStorageService.cs`)**: Replaced bare `catch` in seed decoding with explicit `catch (Exception ex)` and warning log before fallback seed regeneration.
- **Command Injection Performance (`CommandRegistry.cs`)**: Optimized signal injection setters using compiled `System.Linq.Expressions` to eliminate `FieldInfo.SetValue` reflection overhead in hot paths.
- **Service Dependency Property Injection (`NexusService.cs`)**: Changed `Context` and `SignalBus` property setters from `private set` to `protected set` for property injection compatibility.
- **Domain Lifecycles Execution (`ContextLifecycleOrchestrator.cs`)**: Enabled dual execution of `IAsyncStartable` and `IStartable` (and stoppable equivalents) when a domain object implements both contracts.

## [0.5.0] - 2026-08-02

### Added
- **Attribute-Based Command Auto-Discovery (`[RegisterCommand]`)**: Decorate command classes with `[RegisterCommand(typeof(MySignal))]` for automatic signal binding during assembly scanning.
- **Convention-Based Binding (`BindInterfacesAndSelfTo<T>()`)**: Automatically bind concrete classes under all user interfaces AND their concrete type sharing one singleton `Binding` instance.
- **Flexible Domain Lifecycles (`IStartable`, `IAsyncStartable`, `IStoppable`, `IAsyncStoppable`)**: Provide startup and teardown lifecycle hooks for non-service domain objects.
- **Scene & Prefab Auto-Injection (`NexusBinding`)**: Attach `NexusBinding` MonoBehaviour to GameObjects or Prefabs for zero-code scene injection with event fallback listening.
- **SafeDestroy Helper**: Safe EditMode (`DestroyImmediate`) and PlayMode (`Destroy`) object destruction across `UIManager` and `ObjectPoolService`.
- **EditMode Test Suite (`StrategicCapabilitiesEditModeTests.cs`)**: NUnit EditMode test coverage for all strategic capabilities passing 100% in Unity TestRunner (174/174 Passed).

## [0.2.0] - 2026-06-28

### Added

- **Nexus Package CHANGELOG**: Package-specific changelog added at `Nexus/Packages/com.nexus.core/CHANGELOG.md` with full 0.2.0 breaking changes and migration guide.
- **CI Workflows**: GameCI-based package validation and Unity test pipelines added under `.github/workflows/`.
- **Regression & Performance Tests**: New tests covering async/sync validation, DI disposal, circular dependency detection, IResettable integration, NetworkSignalBus rollback, and zero-GC steady-state dispatch.

### Changed

- Package metadata updated to version `0.2.0`.
- Repository vs. package naming clarified in README documentation.

## [0.1.0] - 2025-06-25

### Added

- **Core Framework**: Feature-complete MVCS architecture for Unity 6
  - `Context` system with automated lifecycle discovery, attribute-based signal registration, and HybridQueue integration
  - `SignalBus` with 4 execution modes: Sequential, Concurrent, Exclusive, Composite Trigger
  - Zero-allocation steady-state via `CommandPool` and strongly-typed generic delegates
  - Dependency Injection container (`NexusDI`) supporting constructor, field, property, and method injection

- **Execution Modes**:
  - Sequential (default): deterministic priority-based ordering
  - Concurrent: parallel async I/O and loading
  - Exclusive: single handler guarantee
  - Composite Trigger: fan-in orchestration waiting for multiple signals
  - `ExecuteWithDecorators` / `ExecuteWithDecoratorsAsync` decorator pipeline support

- **Observability & Debugging**:
  - `NexusTracer` — zero-allocation causal tracing showing signal → command → sub-signal chains
  - `NexusInspector` — Editor window with Time Travel Debugging UI
  - `DashboardPlugin` — Editor dashboard for live signal monitoring
  - `TracerPlugin` — live trace sink with real-time log view
  - `ExplorerPlugin` — signal/callback registration explorer
  - `HierarchyPlugin` — context hierarchy visualizer
  - `WizardPlugin` — Root GameObject setup wizard
  - `TypeAnalyzerPlugin` — assembly reflection analyzer

- **Build Validation** (`Nexus -> Validate Architecture`):
  - Priority conflict detection
  - Mixed-mode execution violation checks
  - Side-effect detection in concurrent commands
  - Model ownership and dependency cycle validation
  - AOT/IL2CPP compatibility

- **Error Recovery**:
  - Customizable strategies: Retry, Fallback, Abort
  - Per-command recovery policy configuration

- **AOT/IL2CPP Support**:
  - `link.xml` with AOT preservation rules
  - `[Preserve]` attributes on key types
  - Generic command support for IL2CPP code-stripping

- **View/Mediator System**:
  - `View` base class with automatic `Mediator` binding via `[Mediator]` attribute
  - `Mediator<TRootView>` with lifecycle hooks (`OnBind`, `OnUnbind`)
  - `ViewBinder` for scene-based view registration
  - Object-pooled mediator instances for zero-allocation view lifecycle

- **Samples**:
  - `Samples~/Counter` — minimal counter app demonstrating 0-GC commands, reactive model, and mediator bindings

- **Project Infrastructure**:
  - UPM package structure (`Packages/com.nexus.core/`)
  - MIT License
  - CI workflow with package validation and Unity test pipeline (GameCI)
  - Issue templates (bug report, feature request)

### Fixed

- `ViewBinder.Bind` / `Unbind` and `_isBound` guard in `View`
- `Context.Dispose` guard and `IContextLifecycle.OnDispose` call
- Skip value types in `NexusDI.Inject` to prevent allocation
- Thread safety in `HybridQueue.GetOrCreateNextFrameQueue`
- `NexusTrace.EndEvent` O(1) with stored buffer index

### Changed

- `NEXUS_DEBUG` compile guards for conditional ProfilerMarker instrumentation
- DI auto-bind on `ISignalBus` in `SignalBus` registration
- Cross-context signal routing with scope tag matching
