# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.1] - 2026-07-10

### Added

- **IUIAssetProvider Interface**: Decoupled UI window asset loading from `WindowManager` to allow swapping default Unity `Resources` system with **Unity Addressables** or **AssetBundles** in game projects.
- **GetLong and SetLong Methods**: Added native `long` data type support to `IPlayerPrefsService` interface and implementations to prevent floating-point precision loss in high-value balances.
- **In-Memory Caching and Focus/Quit Hooks for EncryptedStorage**: Added an in-memory cache to `EncryptedStorageService` to prevent synchronous I/O freezes on every write. Changed default `AutoSave` behavior to only write to disk on `Save()` or application focus loss/quit.
- **WindowManager Integration Tests**: Added `WindowManagerAssetProviderTests` to verify custom UI asset providers resolve correctly from the DI container.

### Changed

- **Economy Balance System**: Updated `EconomyService` load/save operations to use new `GetLong` and `SetLong` methods to prevent precision loss for currency amounts exceeding `16,777,216`.

### Fixed

- **Encrypted Storage Unit Tests**: Fixed `EncryptedStorageService_TamperDetectionRejectsCorruptedFile` and `EncryptedStorageService_DeviceBindingRejectsForeignSaveFile` tests to use `AutoSave = true` (or create a fresh instance) to correctly verify disk manipulation bypassing memory cache.

## [0.3.0] - 2026-07-07

### Added

- **Enhanced Service Infrastructure**: Expanded service suite with 11 production-ready services including Audio, Localization, Feedback, Storage, Tick, Analytics, Ads, IAP, Economy, Progression, and Object Pool services
- **GameSaveManager Extension**: JSON-based save/load system with ISaveDataProvider interface for model state serialization
- **SaveThrottler Service**: Throttled save operations with ITickService integration and configurable time windows
- **Localization Service**: Multi-language support with RTL (Right-to-Left) text reversal for Arabic, Hebrew, Farsi, and other RTL languages
- **Feedback Service**: Unified haptic and audio feedback system with preset-based feedback (LightClick, MediumImpact, HeavyImpact, etc.)
- **Enhanced Editor Plugin System**: 11 modular editor plugins including Dashboard, Explorer, Tracer, GameManager, Graph, Hierarchy, TypeAnalyzer, and more
- **NexusWindow Architecture**: Centralized editor window with plugin-based architecture for extensibility
- **Live Reload Processor**: Runtime hot-reload support for faster iteration during development
- **Build Validation System**: CI/CD-friendly validation with assembly scanning and compile-time error detection
- **Type Dependency Analyzer**: Visual tool for analyzing type dependencies and injection graphs
- **DOTS Bridge**: Integration layer for Unity ECS (Data-Oriented Technology Stack)
- **GameStateMachine**: Finite state machine implementation for game state management
- **CompositeTriggerState**: Support for composite trigger states in signal handling
- **Enhanced Testing Infrastructure**: Comprehensive test harness with NexusTestContext, MockContext, and runtime test utilities
- **SecureObservableProperty**: Encrypted observable property variant for sensitive data
- **NetworkSignalBus**: Enhanced netcode support with INetworkSignal interface for multiplayer scenarios
- **HybridQueue**: Thread-safe and next-frame queue implementations for cross-thread signal dispatch
- **Recovery System**: IRecoveryStrategy interface for graceful error handling and fallback mechanisms
- **SceneManagerExtensions**: Unity SceneManager utilities for scene lifecycle management
- **DebugHUD**: Runtime debugging heads-up display for development
- **VersionedScriptableObject**: Version-aware ScriptableObject base class for data migration

### Changed

- **Breaking — Service Interface Updates**: Several service interfaces updated with additional methods (IPlayerPrefsService now includes GetBool/SetBool, IAudioService now includes PlaySfxAtPosition)
- **Breaking — IReactiveModel Signature**: OnBind method signature changed from `void OnBind(IContext)` to `ValueTask OnBind(CancellationToken)` for async initialization support
- **Improved MockContext**: Centralized mock context implementation in Runtime/Testing/ namespace for test reusability
- **Enhanced SignalBus**: Added FireAsyncWithTimeout and FireAsyncAndForget methods for better async control
- **Optimized Subscription Management**: Improved subscription node pooling and cleanup
- **Context Lifecycle**: Enhanced IContextLifecycle with better cancellation token support
- **NexusRuntime Metrics**: Added production tracing ring buffer and per-second rate tracking
- **DI Container Improvements**: Better circular dependency detection and singleton tracking

### Fixed

- **Test Compilation Errors**: Fixed missing using directives and interface implementations in test fixtures
- **Mock Service Implementations**: Corrected mock implementations to match updated service interfaces
- **SaveThrottler Test**: Fixed async/await warning and updated test to use correct API
- **GameSaveManager Test**: Updated to use ISaveDataProvider instead of IReactiveModel
- **QueueTests MockContext**: Fixed MockContext reference to use centralized implementation
- **FeedbackService Mocks**: Added missing IsEnabled property and PlaySfxAtPosition method to mock services

### Documentation

- **Added LICENSE**: MIT License for open-source distribution
- **Added README.md**: Comprehensive documentation with quick start guide, architecture overview, and examples
- **Added Migration Guide**: Version upgrade and migration instructions
- **Added Troubleshooting Guide**: Common issues and solutions documentation
- **Enhanced CHANGELOG**: Detailed changelog following Keep a Changelog format

### Migration Guide (v0.2.0 → v0.3.0)

1. **Update IReactiveModel implementations**: Change `void OnBind(IContext)` to `ValueTask OnBind(CancellationToken ct)`
2. **Update service mock implementations**: Add new methods to mock services (GetBool/SetBool for IPlayerPrefsService, PlaySfxAtPosition for IAudioService, IsEnabled for IHapticService)
3. **Review service interface changes**: Check all service implementations for new method signatures
4. **Update test fixtures**: Use centralized MockContext from Nexus.Core.Testing namespace
5. **Regenerate AOT binder**: Run `Nexus > Generate AOT Binder` after updating to get new service injectors
6. **Review async signal handling**: Use FireAsyncWithTimeout for timeout-sensitive operations

## [0.2.0] - 2026-06-28

### Added

- **0-GC Concurrent ArrayPool Integration**: Concurrent command execution now uses `ArrayPool<ValueTask>` to batch async dispatches without heap allocations.
- **AOT/IL2CPP link.xml Auto-Generation**: `NexusCodeGenerator` now emits a `link.xml` covering all injected types to prevent code stripping on WebGL/console platforms.
- **Static Field/Property Caching in Generated Binder**: Code-generated injectors cache private `FieldInfo`/`PropertyInfo`/`MethodInfo` lookups in static readonly fields for zero-allocation AOT injection.
- **Circular Dependency Detection**: `NexusDI.Resolve` now tracks `_constructingSingletons` and throws `InvalidOperationException` if a singleton resolves itself during construction.
- **IDisposable Singleton Tracking**: `BindInstance` supports `disposeWithContainer` flag and tracks all bound singletons for deterministic disposal.
- **IResettable Integration**: `ClearInjectedReferences` now calls `Reset()` on pooled objects implementing `IResettable`.
- **Thread-Local Stack Depth Tracking**: Reentrancy guard uses `AsyncLocal<int>` to correctly track depth across async dispatches.
- **Configurable CodeGen Output Paths**: `NexusEditorSettings` exposes `BinderOutputPath` and `LinkXmlOutputPath` for generated assets.
- **Auto-Generated .gitignore for Generated Assets**: Code generator writes `.gitignore` entries for generated binder and link.xml files.

### Changed

- **Breaking — `SignalBus.Fire` async handler protection**: Calling synchronous `Fire()` on a signal with async handlers now throws `InvalidOperationException` in `UNITY_EDITOR` and `DEVELOPMENT_BUILD`, and logs an error before safe-fallback in release builds. Use `FireAsync()` or `FireAsyncAndForget()` instead.
- **Breaking — `ICommand` / `IAsyncCommand` mutual exclusivity**: A command class implementing both interfaces will now throw `InvalidOperationException` during registration.
- **Breaking — `FireAsyncAndForget` return type**: Changed from `async void` to `async ValueTask` for better exception observability. Existing fire-and-forget call sites continue to work.
- **Async composite commands use `SafeAsyncRunner`**: `ExecuteCompositeCommandAsync` no longer uses `async void`; exceptions are routed through the central `SafeAsyncRunner` pipeline.
- **`SignalBus.Dispose` lock safety**: Subscription cleanup now acquires `_subLock` to prevent race conditions with concurrent unsubscribe operations.
- **`Context.Dispose` lock safety**: Plugin snapshot iteration during dispose is performed under a copied list to avoid lock-ordering issues.
- **`ProcessCompositeTriggers` concurrency**: Composite trigger bitmask updates are now guarded by `_compositeLock`.

### Fixed

- **Stale generated binder cleanup**: Default-path stale binder is deleted when output path changes.
- **Value-type injection compile-time validation**: Code generator throws explicit errors for `[Inject]` fields/properties/parameters of value types.
- **Release-build null-ref in async bridge**: Safe-fallback path in `FireInternalAsyncFromSync` now handles null contexts gracefully.

### Migration Guide (v0.1.0 → v0.2.0)

1. **Update `Fire()` calls for async-handled signals**: If a signal has `SubscribeAsync` or `IAsyncCommand` handlers, replace `Fire()` with `FireAsync()` (await) or `FireAsyncAndForget()`.
2. **Remove duplicate interface implementations**: Ensure command classes implement only `ICommand` *or* `IAsyncCommand`, never both.
3. **Review generated files**: If you customized the binder path, update `NexusEditorSettings` and remove stale files from the old default path.
4. **AOT builds**: Validate that the generated `link.xml` is included in your build; no manual preservation attributes are required for injected members anymore.
