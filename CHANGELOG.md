# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **`Root.SetUp`/`RegisterLifecycle` silent no-op after Awake (`Root.cs`)**: both now throw `InvalidOperationException` when called after the context was already created — previously the configuration was silently dropped (the demo's boot path calls them before `SetActive(true)`, so it is unaffected).
- **`Context` double-Configure guard conflated harness builder (`Context.cs`)**: the guard keyed on `_builder != null`, which is also set by `GetOrCreateBuilder()` (the `NexusTestContext.Builder` harness path) — so binding through the harness and then calling `Configure()` silently skipped validation/scanning/lifecycle discovery. The guard now keys on a dedicated `_configured` flag; a pre-built harness builder is reused when present.
- **SaveThrottler Multi-Owner (`SaveThrottler.cs`)**: Replaced the single pending-slot design with per-owner slots (`TryRequestSave(owner, ...)` / `ForceSave(owner, ...)`). Previously, when two services (Economy + Progression) shared one throttler singleton, the last `TryRequestSave` silently dropped the other service's pending write — real data loss. Failure backoff/retry-cap is now isolated per owner, and `Flush()` persists every owner.
- **`[OptionalInject]` Never Injected (`NexusDI.cs`)**: `OptionalInjectAttribute` does not derive from `InjectAttribute`, so members decorated only with `[OptionalInject]` were absent from the injectable metadata and were never injected — even when a binding existed (e.g. `EconomyService.SaveThrottler` stayed null, silently disabling write-coalescing). Injectable and clearable metadata now accept both attributes; missing optionals are still skipped by validation and strict injection.
- **EncryptedStorageService DI Construction (`EncryptedStorageService.cs`)**: Added a parameterless constructor delegating to the default salt. The sole optional-string constructor made strict injection fail on the unresolved `System.String` parameter, breaking any container that bound the service.
- **Harness `ValidateOnStartup` leak (`FullArchitectureStressSuite.cs`)**: the stress suite opted out of startup DI validation for its own late-binding tests but never restored the static flag, so every later suite silently ran with validation disabled. `Run()` now restores the framework default in a `finally` block.
- **AOT Dashboard false warning (`NexusCodeGenerator.cs`)**: `HasInjectableTypes` scanned fields/properties but not method-level `[Inject]`, so a type injecting only via methods produced a false "AOT generation disabled" Dashboard warning.
- **AOT Binder Generator (`NexusCodeGenerator.cs`)**: mirrors the `[OptionalInject]` metadata fix for the AOT/IL2CPP path — optional-only members were previously excluded from the generated injectors (never injected even when bound) and `di.Resolve<T>()` was emitted for them (throwing at boot when unbound, e.g. `INetworkEconomyValidator`/`ILocalizationTableProvider` in the demo). Optional members/params now emit `di.TryResolve<T>()` (null when unbound). `NexusGeneratedBinder.g.cs` was hand-synced to the fixed output — **regenerate it in the Unity editor** (`Nexus → Generate AOT Binder`) so the AOT path matches.

### Added
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
