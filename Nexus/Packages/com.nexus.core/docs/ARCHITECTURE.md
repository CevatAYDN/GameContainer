> **Who this is for:** level 3 and contributors. How contexts, containers, the signal bus and the editor host fit together, with the hardening contracts the runtime guarantees.

> **For AI Agents:** This document is the source of truth for the Nexus Core runtime architecture, container lifecycles, signal dispatch pipelines, and editor host interactions. Before making architectural changes:
> 1. Read [README.md](../README.md#glossary) for domain terminology.
> 2. Inspect [Context.cs](../Runtime/Core/Context.cs) and [SignalBus.cs](../Runtime/Core/SignalBus.cs).
> 3. Run EditMode infrastructure tests: `InfrastructureValidationTests`.
>
> **Out of scope:** Third-party package integrations.

# Nexus Architecture Guide

## Hardening contracts

- Optional named-binding, cross-boundary and lazy-service maps are created on first registration under the disposal lock and published with volatile memory ordering. Missing lookups/validation do not instantiate them; named lookup rules, lazy reference deduplication and standard dictionary concurrency remain unchanged.

- Generic DI cache entries represent locally registered non-transient instances; local registration generations invalidate them and every resolve checks disposal. Adapters and inherited bindings retain their resolution ownership.
- Observable notifications preserve the last delivered old/new pair. Each list owns its reentrant drain buffer; callbacks run outside its state lock.
- Derived properties version computations before invoking selectors (#346), reject older results after a newer successful one, and serialize/coalesce publication outside the lifetime lock. Initial publication cannot overwrite a concurrent source result; disposal drops pending results and detaches sources.
- SaveThrottler tracks request versions so completing an older save cannot clear a newer request. Storage migration checks the key version before its coordinated disk commit.
- SaveThrottler clears the due buffer owned by each Tick in finally (#349), so thread-static capacity does not retain disposed save owners. Nested ticks keep separate work buffers.
- Progression reads both saved levels before publication; unreadable/malformed state fails initialization without writes and blocks mutations until recovery (#402). Disposal ends persistence ownership before any delayed callback can write over a replacement (#404).
- Storage path-query failures do not establish absence or select stale backups (#403). Failed-read cache publication validates the observed key generation, including completed deletions (#406). Game-save ordering is assigned before context hops/user capture (#405); invalid saved monotonic counters grant no offline progress (#407).
- The in-memory testing store separates live values from confirmed snapshots (#351). Failed saves commit nothing; reopen discards pending writes/deletes. Unreadable state survives reopen until restored or successfully replaced. This single-threaded fake is not proof of encrypted multi-file atomicity.
- Network rollback retains external inputs and regenerates handler-derived payloads, branches and future schedules (#348). External future inputs are scheduled at their recorded tick; already-due work runs before that tick's new external calls. Identity is separate from corrected production tick/order, which also orders the public typed-history surfaces. Pending schedules produced before rollback survive a partial replay even when their parent was pruned; external predictions beyond the target are removed. Snapshots after the restored point are invalidated, then rebuilt only for replayed ticks. A handler cannot schedule into an earlier tick. Drain incoming network work before advancing ticks, firing any owner input or starting rollback; asynchronous handlers cannot provide deterministic snapshots.
- The public history replay API owns one reusable snapshot per active recursion depth (#344). Callback-driven Clear/Prune/nested Replay cannot overwrite an outer snapshot; each buffer is cleared and its depth released in finally.
- Context teardown guarantees container cleanup/unregistration even when cancellation callbacks fail. Economy ignores late reconciliation once teardown begins.
- Optional Firebase services gate continuations/admission after disposal or cancellation (#385); analytics discards properties under denied consent (#393), and Remote Config removal updates its snapshot and notifies the key (#395). Controlled SDK tests do not establish device collection behavior.
- DI claims teardown before callbacks without prematurely closing dependency resolution (#356). Failed unreturned transient/factory products are ended (#371); successful products retain caller ownership. Singleton alias updates retain a shared compatible binding (#359), and named lazy initialization follows the named binding owner (#373).
- Accepted lifecycles retain cleanup ownership even after configuration fails (#365). Runtime-created settings are owned before registration publication (#377). View/mediator acquisition checks cancellation across bind callbacks (#362); failed prefab injection never activates an unreturned clone (#368).
- Dispatch cancellation failures cannot bypass owned-state cleanup (#361); subscription construction does not publish an already-cancelled node (#364). Composite retry/timeout and fallback-abort decisions are consistent with ordinary commands (#357, #367, #370). Async tree-depth updates are atomic (#374).
- Completed composite dispatch buffers release their references while caller-retained snapshots remain valid (#376). A queue clear ends the current drain generation (#378); plugin sink publication/removal shares the ownership critical section (#379).
- A superseded FSM enter that fails late still awaits its own exit before releasing the transition queue (#347), including when Dispose superseded it. It cannot run a stale error-state fallback or let a newer state overlap that cleanup.
- `ContextFactory.StartAsync` is an explicit, scan-free configuration/initialization entry point. Existing Root and discovery APIs remain supported.
- `BindComposite<TFirst,TSecond,TCommand>` uses value payloads and the existing command pool/recovery pipeline. Arbitrary-arity `CompositeContext` retains immutable snapshot semantics and its allocation cost.

- Command pools retain local transient factories/overrides and construct defaults in their owning DI scope. Cached DI instances are never admitted into a transient pool.
- GameObject pool cleanup marks every entry-snapshot pool before callbacks (#345), including pools visited later by ClearAll. Reentrant spawns belong to replacements and are not removed by old cleanup. Acquisition revalidates after activation/parenting and OnSpawned; a cancelled or replaced lease is not returned. Despawn revalidates after deactivation/parenting before retaining an instance. Disposal closes acquisition first and destroys the service root in finally; prewarm cancellation also covers its final yield.
- Fresh pool objects are staged inactive and owned before activation (#360). A retiring framework-owned UI canvas is undiscoverable before deferred destruction (#358). Audio fade runners are service-owned on shared roots; live effect gains preserve per-play volume across channel changes (#363, #366).
- Runtime/AOT assembly catalogs exclude test support and editor implementation assemblies. Trace time is worker-safe monotonic elapsed time from the Nexus trace clock, independent of Unity startup time.
- Editor binder conditions ignore comment/literal spans and fail closed for unmatched or ambiguous source declarations (#354). Conditional partial members retain reflection instead of receiving an unconditional injector/constructor factory; the generated header records the fallback and link.xml preserves it.
- Binder output-path migration validates the old file's generated ownership and retires it only after replacement outputs succeed (#401); destination refusal must not delete the previous script or meta.
- Generated cache fields use unique emission identities rather than sanitized type/member names (#408). Both generators escape keyword members and line-separator literals; the editor uses the C# type formatter for array ranks and keyword types. Source/shipped-analyzer parity is executable, not inferred from a rebuild.

- DI validation reads actual default/named registrations and each constructing binding's owning scope. It validates every alias, compatible constructor overrides, eager dependency cycles and captive lifetimes without executing constructors/factories. Opaque factories/instances/adapters and lazy edges retain documented limits.
- A singleton cycle whose two halves are first resolved on two threads at once is reported after 10 s as `Timed out waiting for concurrent construction of singleton …`, not as a cycle.
- Loading-time `Context.Prewarm<TSignal>()` populates idle command capacity and prepares injection metadata without firing commands, subscribers or one-shots. Pool ownership covers idle and active leases; Clear cannot allow a factory to hand out an already-rented object.
- Mono/JIT caches suitable constructor delegates once, avoiding reflection argument arrays. Open generic/value/optional/override cases keep their fallback; IL2CPP/AOT never emits dynamic IL.
- Registry registration occurs during construction; it is **not** readiness. `OnContextConfigured` follows successful bindings/validation and `OnContextInitialized` follows full async startup. NexusBehaviour defers injection/Awake hooks until configuration and Start hooks until async initialization. It clears context-owned subscriptions/injected references on replacement or disposal. A nearest parent Root is authoritative; unrelated scope events cannot redirect injection.
- Post-context dispatch skips contexts whose teardown has begun and checks again between callbacks, so completing another Root's startup cannot run callbacks after the target's `OnDispose` (#337).
- One persistent Global Root is supported. Serialized global flags are discovered on cold startup before linking orphan roots; duplicate globals are rejected. Hierarchy menus configure inactive GameObjects before Awake, and global roots stay top-level.
- Economy currencies promote once from long to canonical BigDouble using the live balance before disk. Existing long observables become saturated/truncated projections. Big backends implement INetworkBigEconomyValidator; unsupported transactions fail closed. Storage uses finite-only invariant round-trip mantissa encoding.
- Receipt-keyed economy earnings (#350) opt a currency into one canonical versioned wallet payload containing its balance and retained receipt ids. Every later mutation preserves that ledger, including promotion. Confirmation bypasses the throttler and uses the economy store override; a failed confirmation leaves live state retryable but must not acknowledge delivery. This is local per-currency idempotency, not atomicity with a separate game blob or backend.
- Wallet staging rejects store-callback mutations before state changes. A previously accepted spend's rejection waits for staging asynchronously instead of losing its refund to that guard. Receipt ids are validated for lossless UTF-8 encoding before mutation.
- `TryGetBalance`/`TryGetBigBalance` (#352) inspect first-load status without manufacturing a balance on failure. Views can bind unavailable and retry after recovery; throwing getters/mutations retain #339's fail-closed contract. Loaded queries use the existing allocation-free balance views.
- A custom input provider owns movement including zero. Optional DOTS bridges (`com.nexus.dots`) complete registered JobHandle producers before each dequeue, reinitialization or disposal; callers register every scheduled producer on the main thread. The per-dequeue fence includes producers scheduled by a dispatched handler (#383).

Verification status: the repository's `docs/RELEASE_GATES.md`.
The repository text-triage script reports matches, not parsed methods or verified
violations (#355); it cannot replace executable or platform-specific verification.

This document describes the high-level architecture, runtime pipeline, component contracts, and lifecycle sequences of the **Nexus Core** framework for Unity 6.

---

## 🏛️ System Architecture Diagram

```
┌───────────────────────────────────────────────────────────┐
│                  Editor Window (NexusWindow)              │
│  ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌───────────┐   │
│  │ Status   │ │ Tracer   │ │Performance│ │ Wizard    │   │
│  └─────┬────┘ └─────┬────┘ └─────┬─────┘ └─────┬─────┘   │
│        │            │            │             │         │
│        └────────────┴────────────┴─────────────┘         │
│                          │                               │
│                    ┌─────▼─────┐                         │
│                    │  OnUpdate │ (~200ms window tick)    │
│                    └─────┬─────┘                         │
└──────────────────────────┼───────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────┐
│                     Runtime (Play Mode)                  │
│                                                          │
│  ┌─────────────┐    ┌─────────────┐    ┌──────────────┐  │
│  │    Root     │───▶│   Context   │───▶│  SignalBus   │  │
│  │ (Scene MB)  │    │(DI+Lifecycle│    │ (Dispatch)   │  │
│  └─────────────┘    └──────┬──────┘    └──────┬───────┘  │
│                            │                  │          │
│                     ┌──────▼──────┐    ┌──────▼───────┐  │
│                     │  Services   │    │   Commands   │  │
│                     └─────────────┘    └──────────────┘  │
│                                                          │
│                  ┌──────────────────┐                    │
│                  │ IContextResolver │ (cross-context)    │
│                  └──────────────────┘                    │
└──────────────────────────────────────────────────────────┘
```

---

## 🔄 Lifecycle Sequence

```
CreateView()          OnEnable()           OnUpdate() (~200ms tick)
    │                     │                        │
    ▼                     ▼                        ▼
Build UI Tree         Subscribe events           Read state
Subscribe callbacks   Register listeners         Render changes
Cache references      Load static caches        (steady state)

OnDisable()               Dispose()
    │                        │
    ▼                        ▼
Unsubscribe events    Stop schedulers
Reset flags           Clear collections
Drain queues          Dispose View / Container
Call base             Call base
```

---

## 🔑 Key Components

### 1. `Context` & `IContextLifecycle`
The central dependency container and signal lifecycle manager. Root and sub-contexts configure models, commands, and services via `IContextBuilder`.
- All context types route through `Context.InitializeLifecycleAsync()`.
- **Decision Rationale:** Unified lifecycle initialization guarantees that pure contexts (used in tests) and root contexts (anchored to scene GameObjects) follow the exact same ordering of DI binding, model initialization, and service startup.

### 2. `SignalBus` & `IContextResolver`
Handles signal registration and command execution.
- **Dispatch Modes**:
  - `Sequential`: Executes commands one at a time, highest `priority` first (handlers of one signal need distinct priorities; a duplicate is rejected at registration).
  - `Concurrent`: Executes commands asynchronously via tasks.
  - `Exclusive`: Ensures only one command instance executes for the signal type.
  - `Composite`: Triggers when multiple dependent signals are satisfied.
  - `Detached`: An async command `FireAsync` starts before the others and does not await, on the context's lifetime token; it may share a signal with any mode but `Exclusive` (#155).
- **Cross-Context Routing**: `IContextResolver` provides clean context lookup without exposing internal global registries.

### 3. `ObservableProperty<T>`
Type-safe reactive property wrapper providing change notifications to UI views and mediators, 0 B after the first subscription (harness GCA2; [Performance scope](../README.md#performance-scope)).

### 4. `NexusEditorPlugin` & `NexusWindow`
Editor tools implement `INexusEditorPlugin`. The host window drives tab updates via `OnUpdate()` cadence (~200ms interval) without custom background timers.

### 5. Service Disposal Contract
A context's `INexusService` singletons end together with its other singletons, newest first, so a consumer is disposed before the services it uses (#197):
- A parent `Context` owns its child contexts and ends descendants before its own cancellation callbacks, lifecycles and services (#337). This follows `Context.Parent`, not Transform hierarchy or Root enablement. Child `OnDispose` callbacks can still resolve and write to parent-owned storage. Disposing a child alone leaves its parent alive; attaching a child after parent teardown starts throws `ObjectDisposedException`.
- `Context.Dispose()` and `DisposeAsync()` run the stoppables, each lifecycle's `OnDispose` and the views' mediators, then every singleton the game's bindings created, in reverse creation order while the container still resolves (an instance the game built and bound counts as created when it is injected), then the plugins, the signal bus, the queue and the pools. The container's own modules end last.
- An `INexusService` gets its `[Deconstruct]` hooks and `OnDispose()`, never `Dispose()` from the container: `NexusService<T>.OnDispose()` calls `Dispose()` itself, so each service ends once.
- A bare `NexusDI` and a child scope (`CreateChildScope`) end the services they hold the same way; until #197 they skipped them.
- Any other singleton gets its `[Deconstruct]` hooks and `Dispose()`. One that is only `IAsyncDisposable` is awaited by `DisposeAsync()`, and `Dispose()` disposes it on one ordered background chain.
- An `IAsyncStoppable` that is not also `IStoppable` is awaited (up to 5 s) by `DisposeAsync()` before the singletons. `Dispose()` (`Root.OnDestroy`, `NexusRuntime.Reset`) does not block the main thread: it starts `StopAsync`, and the part after its first incomplete await runs while or after the singletons are disposed, so it must not use injected services; a stop still running then is named in a `WarnAlways` line (#291).
- Parent `DisposeAsync()` joins child context teardown before closing parent services, including a child's already-running `DisposeAsync()`. Choose async disposal from the start when asynchronous cleanup needs parent services: a child's prior synchronous `Dispose()` may already have handed off background cleanup, and a later async call cannot upgrade or join that background work. Synchronous parent `Dispose()` cannot join independently running child context teardown: it warns and continues. Repeated disposal calls on the same context remain no-ops; await the first async call for completion. A child's cleanup must not await its ancestor's shutdown from inside its own teardown, which would form a circular wait.

### 6. Recovery Fallback Dispatch
Both recovery paths dispatch fallback commands through `CommandExecutor`'s object-based `Execute(CommandHandlerInfo, object)` / `ExecuteAsync(CommandHandlerInfo, object, CancellationToken)`, which support **generic-only** commands (`ICommand<TSignal>` / `IAsyncCommand<TSignal>` that do not implement the non-generic interface) via cached reflection dispatchers. The sync error handler additionally rejects async-only fallback types (it cannot await them) to avoid re-entering the same strategy decision endlessly — those are logged and treated as `Skip`.

### 7. DI Validation Scope
`ContextBuilder.Validate()` validates the *concrete* implementations of interface bindings (`Bind<TInterface, TImplementation>`) — not just the interface keys — so missing constructor/`[Inject]` dependencies are reported for the types that are actually constructed. `LazyInjection<T>` fields are excluded (the injector constructs them directly).

### 8. Strategic Capabilities (added in 0.5.0)
- **Attribute-Based Auto-Discovery (`[RegisterCommand]`)**: Commands decorated with `[RegisterCommand(typeof(TSignal))]` are auto-bound during assembly scanning.
- **Convention-Based Binding (`BindInterfacesAndSelfTo<T>`)**: Polymorphic binding where all user-defined interfaces and the concrete type share ONE `Binding` instance.
- **Flexible Domain Lifecycles (`IStartable`, `IAsyncStartable`, `IStoppable`, `IAsyncStoppable`)**: Domain presenters and controllers receive lifecycle callbacks without implementing `INexusService`. A singleton bound with such a type starts after every `OnStartAsync` even when nothing resolved it (a lazy service once something has), and a start that throws fails the context's start-up.
- **Scene Auto-Injection (`NexusBinding`)**: Scene component auto-injection with `Awake`/`Start` triggers and event-driven registration fallbacks.

---

## 🚫 Architectural Anti-Patterns

### ❌ Indexing `ActiveContexts` for Cross-Context Signals
```csharp
// BAD: Direct static access to internal context list
NexusRuntime.ActiveContexts[0].SignalBus.Fire(signal);

// GOOD: Address the context by its ScopeTag (null when no such context is active)
NexusRuntime.GetContext("Gameplay")?.SignalBus.Fire(signal);
```
*Why:* Direct index access creates brittle dependencies and fails during unit testing or multi-context scene reloads.

### ❌ Non-Idempotent `OnDisable` Cleanups
```csharp
// BAD: Assumes OnEnable was called first
public override void OnDisable()
{
    _subscription.Unsubscribe(); // Crashes with NullReferenceException if OnEnable failed
}

// GOOD: Defensive null-checks
public override void OnDisable()
{
    if (_subscription != null)
    {
        _subscription.Unsubscribe();
        _subscription = null;
    }
    base.OnDisable();
}
```
*Why:* Unity Editor can invoke `OnDisable()` during domain reloads or window resets even if `OnEnable()` was interrupted.

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Main framework index and decision flows
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development and edge cases
- 📜 [CHANGELOG.md](../CHANGELOG.md) — Version history

---

**Re-review trigger:** Any change to `Runtime/Core/Context.cs` or `Runtime/Core/SignalBus.cs`.
