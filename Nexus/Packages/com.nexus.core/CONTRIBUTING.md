# Contributing to Nexus Core

## Concurrency Style Guide

Nexus runs on Unity's main thread but exposes thread-safe surfaces
(`FireThreadSafe`, network rollback, worker-thread services). Shared state must
pick ONE documented pattern per structure — mixing patterns in one type is the
most common source of subtle bugs. This guide is the single source of truth.

| Rule | When to use | Example |
|------|-------------|---------|
| **R1 — Publish-through-snapshot** | Read-heavy state read on the hot path, mutated rarely | `SignalBus` subscription/command read copies (`_commandHandlersReadCopy`, `_subscriptionsReadCopy`) |
| **R2 — Lock + plain collection** | Write-heavy or compound (check-then-mutate) operations | `NexusDI._singletonLock` + `HashSet<Binding>`; `EconomyService._balances` mutations |
| **R3 — Interlocked counters / CAS flags** | Lock-free simple counters or one-shot claims | `Metrics` totals, `s_monitoringInitialized`, `HybridQueue` drain guards |
| **R4 — Never invoke callbacks under a lock** | Any event/plugin/sink invocation | `NexusTrace` sink writes (snapshotted under lock, invoked outside); `NexusRuntime` context events (detached before lock) |

### Anti-patterns to avoid

- **`lock (concurrentCollection)`**: locking a `ConcurrentDictionary`/`ConcurrentQueue`
  itself is legal but misleading — the object's own sync primitive is internal.
  Use a dedicated `private readonly object _xxxLock = new()` and document it
  (e.g. `EconomyService._balances` mutation lock).
- **Check-then-set on a `volatile` flag**: two threads can both pass the check.
  Use `Interlocked.CompareExchange` (claim) or a single `lock` scope.
- **Double-reset on pooled objects**: decide whether reset happens on pop or on
  return, not both. If both are deliberate (defensive `Mediator.Reset`), the
  reset MUST be idempotent and the double-call MUST be documented on the method.

### Lifecycle ownership

- `ContextData` is **caller-owned** (scene/asset) unless created at runtime by
  `NexusRuntime.CreatePureContextAsync`, which marks it owned (`Context.OwnsContextData()`)
  and destroys it on dispose. Never destroy caller-provided assets.
- `INexusService` lifecycle (`InitializeAsync`/`OnDispose`) is owned by the
  owning `Context`; `NexusDI.Dispose` skips `INexusService` instances to avoid
  double-dispose. Non-service interfaces (e.g. `IPlayerPrefsService`) keep their
  own `IDisposable` contract — document which one a type uses.
