# Nexus Core Walkthrough

A step-by-step tour of `com.nexus.core` — from first setup to production hardening.
This document is the **hands-on companion** to [README.md](README.md) (overview),
[ARCHITECTURE.md](docs/ARCHITECTURE.md) (module design), and
[10_MIN_QUICKSTART.md](docs/10_MIN_QUICKSTART.md) (fastest path to a running context).

---

## 1. Setup

1. Add the package to your Unity 6 project via **Package Manager → Add from disk**
   (or a git dependency), or open the bundled starter scene
   `Assets/Scenes/NexusStarter.unity`.
2. The `Nexus` folder ships with `NexusEditorSettings.asset`,
   `GameContextData.asset` and a demo `Lifecycle` for reference.
3. Open **Tools → Nexus** (Dashboard). If the Dashboard shows no assemblies,
   check the [TROUBLESHOOTING.md](TROUBLESHOOTING.md) "AOT / assembly scan" section.

## 2. Your first context

A `Root` MonoBehaviour creates a `Context` in `Awake` and drives its async
lifecycle (`OnConfigure → OnInitializeAsync → OnStartAsync`) with priority and
parent-child support. You can also create a code-only context:

```csharp
var context = await NexusRuntime.CreatePureContextAsync("Gameplay");
context.GetOrCreateBuilder().BindService<IMyService, MyService>();
await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, context.LifetimeToken);
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

- `GameSaveManager` writes model snapshots atomically (`File.Replace` /
  overwrite-rename) — never delete-then-move.
- `EncryptedStorageService` provides AES-256 + HMAC-SHA256 integrity,
  device-bound keys, and atomic writes. Tampered payloads are rejected on read.
- `OfflineTimeCalculator` validates offline reward time against **both** the wall
  clock and hardware monotonic ticks (`Environment.TickCount64`), so a device
  clock set backwards **or forwards** cannot inflate rewards.

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

- The signal bus aborts runaway chains with `NexusReentrancyException` at depth
  **10 in every build** (Debug AND Release) — silent returns are not allowed.
- `RecoveryEngine` triages command failures (skip/abort/fallback/retry) through a
  single `BuildPlan` decision tree; a custom `IRecoveryStrategy` can override it.

## 9. Validating your project

- Run the standalone harness: `dotnet run --project tools/nexus-benchmark`
  (207+ regression tests covering registry parity, zero-GC hot paths, recovery,
  threading, and storage integrity).
- In Unity, run the **EditMode tests** (Package Tests → `Nexus.Tests.Editor` /
  `Nexus.Core.Tests`) and call `BuildValidation.RunSilent()`.
- The Dashboard surfaces DI validation warnings, including
  **`[CaptiveDependency]`** (a singleton capturing a transient) and
  **`[ConstructorExplosion]`** (>6 ctor parameters).

## 10. Further reading

| Topic | Doc |
|---|---|
| Architecture | [ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Game patterns | [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) |
| Plugin development | [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) |
| Stability & threading | [STABILITY.md](STABILITY.md) |
| Review findings (A1–B8) | [REVIEW_FINDINGS_A1_B8.md](docs/REVIEW_FINDINGS_A1_B8.md) |
| Troubleshooting | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |
