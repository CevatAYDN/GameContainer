# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
