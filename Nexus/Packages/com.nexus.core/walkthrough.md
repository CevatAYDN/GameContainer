> **Who this is for:** level 1, you know the basics from the quickstart and are building real features. This is the tour from setup to hardening; `docs/CANONICAL-PATTERNS.md` gives the one recommended form of each API you meet here.

# Nexus Core Walkthrough

A step-by-step tour of `com.nexus.core` — from first setup to production hardening.
This document is the **hands-on companion** to [README.md](README.md) (overview),
[ARCHITECTURE.md](docs/ARCHITECTURE.md) (module design), and
[10_MIN_QUICKSTART.md](docs/10_MIN_QUICKSTART.md) (fastest path to a running context).

---

## 1. Setup

Ordinary DI setup no longer allocates optional named/cross-boundary/lazy-service tables. They appear on first registration, without an opt-in flag or API migration. See [performance comparison](docs/PERFORMANCE_COMPARISON.md) for measured workloads; local validation requires no CI.

Choose an [integration path](docs/INTEGRATION_PATHS.md) ([Türkçe](docs/INTEGRATION_PATHS_TR.md)).
The localized Setup Wizard opens this guide from the resolved UPM package location.
After startup, call `context.Prewarm<YourSignal>(4)` during loading to prepare pools
without executing commands. For low-level DI, inspect `container.ValidateBindings()`
before resolving feature objects; factories and supplied instances are opaque boundaries.


For an existing Unity 6 project, start with [GETTING_STARTED.md](docs/GETTING_STARTED.md).
The code-only path awaits `ContextFactory.StartAsync("Gameplay", builder => { /* bindings */ })`;
configuration precedes validation and model/service initialization. Dispose the returned context
when its owner ends. Do not add bindings after startup and call initialization a second time.


1. Add the package to your Unity 6 project via **Package Manager → + → Install package from
   git URL…** pinned to a commit ([README.md](README.md#install)), or **Install package from disk…**
   for a local clone. The package ships no scene: `Assets/Scenes/NexusStarter.unity` exists only in
   this repository's development project (`Nexus/`).
2. `NexusEditorSettings.asset` and `GameContextData.asset` are development-project files too, not
   package content; in your project **Nexus → Editor Settings** creates
   `Assets/NexusEditorSettings.asset` on first use. The demo was removed in 0.5.0; the Setup
   Wizard generates the example project.
3. Open **Window → Nexus → Dashboard** (`Ctrl+Shift+N`). If code generation or the assembly scan
   misbehaves, see [AOT Code Generation Errors](TROUBLESHOOTING.md#aot-code-generation-errors).

## 2. Your first context

A `Root` MonoBehaviour creates a `Context` in `Awake` and drives its async
lifecycle (`OnConfigure → OnInitializeAsync → OnStartAsync`) with priority and
parent-child support. You can also create a code-only context:

```csharp
var context = await ContextFactory.StartAsync("Gameplay", builder =>
{
    builder.BindService<IMyService, MyService>();
});
// Keep the owner alive while the game uses it; await context.DisposeAsync() at shutdown.
```

**Bind anything you resolve.** NexusDI requires every injected dependency to be
registered (strict mode throws; non-strict logs and leaves the member null, then
retries on `ReInjectAll`).

## 3. Signals, commands, and observers

- **Fire signals** with `SignalBus.Fire(new MySignal(...))`. Sync signals with
  async handlers throw `NexusSyncAsyncMismatchException` — use `FireAsync` /
  `FireAsyncAndForget` when handlers are async.
- **Commands** implement `ICommand<T>` / `IAsyncCommand<T>` and are discovered
  via `[SignalHandler]` / `[RegisterCommand]` attributes, or bound fluently with
  `BindCommand<TSignal, TCommand>()`.
- **Reactive models** use `ObservableProperty<T>`; subscribe with
  `property.OnChanged((oldV, newV) => ...)`.
- **Mediators** subscribe in `OnBind()`; every subscription is auto-disposed on
  `Unbind()`/`Reset()` — never subscribe in the constructor.

## 4. Views and mediators

```csharp
[Mediator(typeof(PlayerMediator))]
public class PlayerView : View { }

public class PlayerMediator : Mediator<PlayerView>
{
    protected override void OnBind()
    {
        Subscribe<ScoreChangedSignal>(s => ExecuteIfViewValid(v => v.SetScore(s.Value)));
    }
}
```

Views register with the nearest `Root` automatically on `OnEnable`; `ViewBinder`
pools mediators and clears injected references on return.

## 5. Services and lifecycles

- `NexusService<T>` services are initialized in registration order and disposed
  in reverse order. `BindLazyService` defers construction to first resolve.
- `IStartable` / `IAsyncStartable` / `IStoppable` / `IAsyncStoppable` provide
  domain lifecycle hooks for non-service objects.
- `IPostContextLifecycle.OnPostContext` runs after ALL contexts initialize
  (cross-context wiring) via `NexusRuntime.FinalizeInitializationAsync`.

## 6. Storage and anti-cheat

- `GameSaveManager` writes model snapshots to a temporary file and swaps it in with
  `File.Replace`; where the platform does not support `File.Replace` it logs a warning and
  falls back to a non-atomic delete-then-move.
- `EncryptedStorageService` provides AES-256 + HMAC-SHA256 integrity and device-bound
  keys by default (`StorageKeyBinding.App` keeps data readable after a backup restore on
  another phone; README, "Services"). It uses atomic replacement where available and a recoverable backup elsewhere.
  Tampered payloads are rejected on read; device power-loss guarantees require testing.
- `OfflineTimeCalculator` validates offline reward time against **both** the wall
  clock and a monotonic clock (`Stopwatch.GetTimestamp`), so within one boot session a
  device clock set backwards **or forwards** cannot inflate rewards; across a reboot the
  monotonic clock restarts and only the wall-clock check and the `maxOfflineSeconds` cap apply.

## 7. Threading rules

- Unity APIs must only be touched on the main thread. `GameSaveManager` marshals
  restores back to the captured `SynchronizationContext`; `Root` throws
  `ThreadStateException` if a lifecycle continuation escapes the main thread.
- Every async API takes a `CancellationToken` and forwards it; contexts cancel
  their `LifetimeToken` on dispose.
- **Teardown is non-blocking.** Prefer `await context.DisposeAsync()`; the sync
  `Dispose()` schedules async-only disposables on the thread pool instead of
  blocking the main thread (no sync-over-async deadlocks).

## 8. Reentrancy and recovery

- The signal bus aborts runaway chains with `NexusReentrancyException` in every build
  (Debug AND Release) beyond **10** nested synchronous or **32** nested async dispatches; the
  two counters are separate, so a sync handler that fires async starts a fresh async budget.
  Silent returns are not allowed.
- `RecoveryEngine` triages command failures (skip/abort/fallback/retry) through a
  single `BuildPlan` decision tree; a custom `IRecoveryStrategy` can override it.

## 9. Validating your project

The repository's `docs/RELEASE_GATES.md` records the latest Unity, device and allocation evidence.
Debug tracing uses a monotonic Nexus clock; compare trace
timestamps with `NexusTrace.TimestampNow`, not Unity startup time. Zero-GC assertions require
tracing disabled and a working positive-control allocation recorder.

- In a clone of this repository, run the host harness: `dotnet run --project tools/nexus-benchmark`
  (registry parity, zero-GC hot paths, recovery, threading and storage integrity). The gates
  and their latest measured numbers are in the repository's `docs/RELEASE_GATES.md`.
- In Unity, run the package tests in the Test Runner: EditMode `com.nexus.core.editor.tests`
  and PlayMode `com.nexus.core.tests` (a project that consumes the package lists
  `com.nexus.core` under `testables` in `Packages/manifest.json` to see them), and call
  `BuildValidation.RunSilent()`.
- The Dashboard surfaces DI validation warnings, including
  **`[CaptiveDependency]`** (a singleton capturing a transient) and
  **`[ConstructorExplosion]`** (>6 ctor parameters).

## 10. Further reading

| Topic | Doc |
|---|---|
| Architecture | [ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Game patterns | [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) |
| Plugin development | [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) |
| Stability policy | [STABILITY.md](docs/STABILITY.md) |
| Migrating from Zenject | [ZENJECT_MIGRATION.md](docs/ZENJECT_MIGRATION.md) |
| Troubleshooting | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |

## Current integration contracts

NexusBehaviour injects after its owning context configures and starts after async
startup; disposed/replaced contexts clean tracked references and subscriptions.
Scene/global scope selection, canonical BigDouble currency promotion, producer job
ownership and Input System configuration are described in the
[integration paths](docs/INTEGRATION_PATHS.md). Native inspector/menu tests exercise
Undo, actual rendering and Awake ordering. See the
[measured DI comparison](docs/PERFORMANCE_COMPARISON.md) for workload-specific results.
