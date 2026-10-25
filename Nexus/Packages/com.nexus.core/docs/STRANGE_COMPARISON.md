# Nexus ↔ StrangeIoC — Structural Comparison Report

This report was prepared by reading [The Big Strange HowTo](https://strangeioc.github.io/strangeioc/TheBigStrangeHowTo.html).
Goal: compare Strange's architectural capabilities line by line with the current
state of Nexus, and collect the **closed gaps** and the **deliberate design
differences** in one place. Every row has a "Nexus equivalent", a "status"
and a "note".

Gaps closed in earlier sessions (with their proving tests). The `BinderSuite`
ids live in the `tools/nexus-benchmark` host harness; the CB, MA and PC ids are Unity NUnit
tests (`Tests/Editor/CrossBoundaryTests.cs`, `Tests/Editor/MediatorBindingTests.cs`,
`Tests/Runtime/PostContextTests.cs`).

| Strange capability | Nexus equivalent | Status |
|---|---|---|
| `Bind(anyKey).To(anyValue).ToName()/.ToSingleton()/.ToFactory()` | `NexusBinder<TKey,TValue>` fluent API | ✅ Closed (`BinderSuite` B1–B9) |
| `[Inject(SomeName)]` named injection | `[Inject(Name = "...")]` + `Bind<T>(name)` / `Resolve<T>(name)` | ✅ Closed (`BinderSuite` DI1–DI6) |
| `[PostConstruct]` | `[PostConstruct]` (with Order) | ✅ Closed (`BinderSuite` P1–P3) |
| `[Deconstruct]` | `[Deconstruct]` (with Order, Dispose/DisposeAsync) | ✅ Closed (`BinderSuite` DC1–DC3) |
| `[Construct]` ctor marking | `[Construct]` (alias, counted together with `[Inject]`) | ✅ Closed (`BinderSuite` CT1) |
| `Bind(X).To<Cmd>().Once()` | `BindCommandOnce` / `BindAsyncCommandOnce` / `.Once()` | ✅ Closed (`BinderSuite` ON1–ON5, ONC1–ONC2) |
| Polymorphic binding (`Bind<A>().Bind<B>().To<Impl>()`) | `BindMultiple<T1,T2[,T3],TImpl>()` (shared singleton) | ✅ Closed (`BinderSuite` PM1–PM3) |
| `crossContextInjectionBinder` | `BindCrossBoundary<TInterface,TImpl>()` + `ResolveCrossBoundary<T>()` | ✅ Closed (`CrossBoundaryTests` CB1–CB8) |
| `ToAbstraction<IMediator>()` | `[Mediator(typeof(Concrete), typeof(IMediator))]` | ✅ Closed (`MediatorBindingTests` MA1–MA8) |
| `PostContexts` (once all contexts are ready) | `IPostContextLifecycle` + `NexusRuntime.FinalizeInitializationAsync()` | ✅ Closed (`PostContextTests` PC1–PC5) |

The sections below examine the areas where Strange is **still different** and the
current Nexus equivalents in those areas. None of these areas is "missing";
some are designed differently on purpose.

---

## 1. Cross-context communication

### StrangeIoC
- `crossContextInjectionBinder` — DI sharing across contexts.
- `crossContextDispatcher` + `Broadcast()` — event broadcast across contexts.
- Contexts are related through a hierarchy/parent chain; a child context can reach
  its parent's bindings.

### Nexus
- The `[CrossContext(scopeTag)]` attribute — marks that a signal is broadcast to
  other contexts (detection cached in the CommandRegistry).
- A signal marked this way is fired with an ordinary `SignalBus.Fire`; the bus also
  forwards it to the active contexts (it gets the contexts through `IContextResolver`, skips itself;
  when a `scopeTag` is given, only to contexts with that `ScopeTag`, matched
  case-insensitively). The broadcast logic is private inside `SignalBus`; the public way
  is the attribute.
- `NexusRuntime.GetContexts(scopeTag)` / `GetContext(scopeTag)` — finding a context
  by name; `ActiveContexts` provides a thread-safe snapshot.

**Status:** ✅ Functional equivalent exists. Sharing DI objects across contexts
is provided by `BindCrossBoundary<TInterface,TImpl>()` + `ResolveCrossBoundary<T>()` —
the Nexus equivalent of Strange's `crossContextInjectionBinder`: the type becomes a singleton in the
context where it is registered with `BindCrossBoundary`, and descendant (child/grandchild) contexts
get it deliberately from the parent chain with `ResolveCrossBoundary<T>()`
(`Tests/Editor/CrossBoundaryTests.cs` CB1–CB8). For signal broadcast there is also the `[CrossContext(scopeTag)]` attribute.

**The conceptual difference is deliberate (least-privilege design):** where Strange says
"everything can be shared", Nexus shares only **explicitly marked** types (`BindCrossBoundary`) and
signals (`[CrossContext]`). Services to be shared can also be registered in a common parent
context, or in the root container with `Lifetime.Singleton`.

---

## 2. Mediator lifecycle and auto-wiring

### StrangeIoC
- `OnRegister()` / `OnRemove()` — hooks called when the mediator is bound/released.
- `mediationBinder.Bind<View>().To<Mediator>()` — the view→mediator map.
- `ToAbstraction<IMediator>()` — binding through an interface.
- `SignalMediator` — a mediator base that subscribes to signals directly.

### Nexus
- `Mediator<TView>` — `OnBind()` / `OnUnbind()` hooks; `View` (cast),
  `[Inject] ISignalBus SignalBus`, `Subscribe<T>()` (auto-disposed), `IsViewValid`,
  `ExecuteIfViewValid`.
- The `[Mediator(typeof(TMediator))]` attribute + `ViewBinder.RegisterView` —
  automatic registration when the view is enabled (OnEnable), the mediator is taken from the pool/resolved,
  `mediator.Bind(view, bus)` is called. **The view is injected BEFORE Bind**
  (guaranteed access to `[Inject]` services inside OnBind).
- Mediator **pooling**: `IResettable.Reset()` is called on every pool pop/return;
  `ClearInjectedReferences` clears on pool return; `OnReset()` clears derived
  state (idempotent contract). There is `PoolPopCount`/`PoolLeakWarnings` telemetry.
- `ScreenMediator<TView>` — adds screen lifecycle hooks (`OnScreenOpened`/
  `OnScreenClosed`) on top; `ScreenView` → derives from `View`, UIManager manages
  layering/pooling and does not touch the mediator by hand.

**Status:** ✅ Functional equivalent exists. The names differ (`OnBind`/`OnUnbind`
vs `OnRegister`/`OnRemove`), but the contract is the same: a hook when the view is bound, cleanup when it is
released. What Nexus **adds**: pooling + leak telemetry + view
validity protection (`IsViewValid`).

---

## 3. `[Deconstruct]` and the cleanup contract

### StrangeIoC
- `[Deconstruct]` — a cleanup hook that runs when an object is destroyed.
- No ordering guarantee; the state of dependencies is not guaranteed.

### Nexus
- `[Deconstruct(Order)]` — runs inside `Dispose()` and `DisposeAsync()`, **before**
  `IDisposable.Dispose`, in ascending `Order`.
- **Exactly-once guarantee:** singletons are tracked in a creation-ordered list that is unique
  by reference (an instance bound under several keys is tracked once); even a shared (polymorphic)
  singleton is deconstructed **once**. If one `[Deconstruct]` throws, the others still run
  (per-method try/catch + log).
- **Dependency state:** in Nexus, deconstruct runs inside the `Dispose` loop that walks the
  singletons in reverse creation order — a consumer is cleaned up before its dependencies
  and its injected references are not cleared, so the dependencies are
  **still non-null** and not yet disposed (injected services can be used safely inside
  `[Deconstruct]`).

**Status:** ✅ Closed and **stronger** than Strange (ordered, exactly-once,
dependencies alive).

---

## 4. Context lifecycle phases

### StrangeIoC
- `ContextStart` → `ContextDestroy` + `PostContexts` (ordering of multiple contexts).
- `ContextView` — the context's MonoBehaviour view.

### Nexus
- `IContextLifecycle`: `OnConfigure(builder)` → `OnInitializeAsync(ct)` → `OnStartAsync(ct)` → on dispose,
  reverse registration order (services).
- `Context.Configure()` auto-discovery (finding lifecycles by convention) + manual
  registration; `NexusRuntime.CreatePureContextAsync` — a code-only context that needs no
  scene (tests/dedicated server).
- The `Root` MonoBehaviour — the context's scene view; view registrations work through Root
  discovery.

**Status:** ✅ Functional equivalent exists. The phase names differ but the ordering contract is
the same. For `PostContexts`, **Phase 3 — `IPostContextLifecycle`**: an `IPostContextLifecycle`
interface separate from `IContextLifecycle` is defined (`OnPostContext(builder)`). Inside `Context.Configure()`
every `IContextLifecycle` instance is cast to `IPostContextLifecycle` and kept in a separate
list. `NexusRuntime.FinalizeInitializationAsync()` walks all active contexts and calls each one's
`RunPostContextAsync()`. After the standard lifecycle completes (OnConfigure →
OnInitialize → OnStart), the PostContext phase fires. **The PC1–PC5 PlayMode tests in `Tests/Runtime/PostContextTests.cs` lock this.**

---

## 5. Signal system

### StrangeIoC
- `Signal<T>` classes + `SignalCommandBinder` — signal→command mapping.
- `signal-to-command` binding with a payload.

### Nexus
- `SignalBus.Fire<T>` / `FireAsync<T>` / `FireAsyncWithTimeout` — generic struct
  signals, sync/async separation (mismatch protection), separate command + subscription
  phases (commands first, then subscribers observe).
- `BindCommand<TSignal,TCommand>` / `BindSignal<TSignal>().To<TCommand>()` /
  `.Once()` / composite (`[CompositeSignalHandler]`).
- **Difference:** Strange defines signals as classes; Nexus as **structs**
  (GC-free hot path). In Nexus, signal types are portable value types.

**Status:** ✅ Closed; the GC difference is deliberate (struct signals + pooling).

---

## 6. Binder flexibility — Strange's remaining advantage

Strange's `Bind(anything).To(anything)` generality is freer than Nexus's type-specific
`BindService`/`BindReactiveModel`/`BindMultiple` APIs.
Nexus has regained this generality with `NexusBinder<TKey,TValue>` (`BinderSuite` B1–B9):
non-MVCS catalogs, config tables and entity definitions live in this binder;
bindings inside MVCS stay on type-specific, controlled APIs.

**Status:** ✅ Closed — generality is kept while discipline (outside MVCS = NexusBinder,
inside MVCS = type-specific API) is preserved.

---

## 7. Strengths Nexus has that Strange does not

- **Zero-GC hot path:** struct signals, pooled command/mediator/subscription
  nodes, snapshot-read-copy (read paths thread-safe without locking).
- **First-class async:** `FireAsync`, `IAsyncCommand<T>`, timeout + cancellation,
  sequential/parallel dispatch guarantees.
- **Pooling + telemetry:** mediator/subscription pools, leak warnings,
  `Metrics` (signal/command rates).
- **Fault tolerance:** recovery engine, interceptors, error collection.
- **Proof by harness:** `tools/nexus-benchmark` — a benchmark group and verification suites
  (`Program.cs` → `RunAll`), working proof of the full architecture.

---

## Summary table

| Area | Strange | Nexus | Status |
|---|---|---|---|
| General Binder | `Bind(any).To(any)` | `NexusBinder<TKey,TValue>` | ✅ Closed |
| Named injection | `[Inject(Name)]` | `[Inject(Name=)]` strict | ✅ Closed |
| `[PostConstruct]` | yes | yes (Order) | ✅ Closed |
| `[Deconstruct]` | yes (unordered) | yes (ordered, exactly-once) | ✅ Closed + stronger |
| `[Construct]` | yes | yes (alias) | ✅ Closed |
| `.Once()` | yes | yes (race-safe) | ✅ Closed |
| Polymorphic binding | yes | yes (shared singleton) | ✅ Closed |
| Cross-context | binder + dispatcher | `[CrossContext]` + scope tag / `BindCrossBoundary<T>` + `ResolveCrossBoundary<T>` | ✅ Closed |
| Mediator lifecycle | OnRegister/OnRemove / ToAbstraction | OnBind/OnUnbind + pooling / `[Mediator(TMediator, TIAbstraction)]` | ✅ Closed + stronger |
| Signal | class-based | struct-based (GC-free) | ✅ Different design |
| PostContexts | global phase once all contexts are ready | `IPostContextLifecycle` + `NexusRuntime.FinalizeInitializationAsync()` | ✅ Closed (PC1–PC5) |
| Async | limited | first-class | ✅ Nexus ahead |
| Pooling/telemetry | none | comprehensive | ✅ Nexus ahead |

**Conclusion:** All closable gaps were closed and proven by tests (host harness and Unity NUnit).
The remaining differences are either deliberate design decisions (struct signals, least-privilege
cross-context) or areas where Nexus is already ahead (pooling, async,
telemetry).
