# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
