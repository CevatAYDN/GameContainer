# Nexus Benchmark Harness

Standalone benchmark harness for the Nexus runtime. Replicates every assertion in
`Nexus/Packages/com.nexus.core/Tests/Runtime/PerformanceTests.cs` plus the
full-architecture stress suite and the recovery regression, so the same numbers can be
produced on plain .NET when the Unity editor (6000.5) is unavailable.

Tracked in the main repo under `tools/nexus-benchmark/`. Run it on any machine after
`git clone` — it compiles the real runtime sources directly, so it always matches the
checked-in runtime.

## Requirements

- .NET 10 SDK (the project targets `net10.0`; C# stays on `LangVersion 9` to match
  the Unity 6000.5 compiler, so the harness can never use language features the
  runtime's Unity build cannot)
- No NuGet dependencies — the harness compiles the real runtime sources directly

## Run

```powershell
dotnet run -c Release                            # benchmarks + recovery + 39 stress + fuzz + cross-context/thread + dogfood session
dotnet run -c Release -- --alloc-diag            # allocation-source diagnostics
dotnet run -c Release -- --pool-split            # pool round-trip split diagnostics
dotnet run -c Release -- --soak [N]              # run the full pipeline N times (default 10), fail on state creep
dotnet run -c Release -- --json                  # additionally emit a machine-readable JSON report of every test result
dotnet run -c Release -- --coverage              # report which runtime files are compiled by the harness vs out of scope
dotnet run -c Release -- --pin-cpu 0             # pin the process to core N + High priority (deterministic ns/op numbers)
```

Flags combine: `dotnet run -c Release -- --json --pin-cpu 2 --soak 20`.
Note: `--pin-cpu` pins the whole process; the cross-thread suite (C2) then time-slices
its 8 workers onto that one core — use it for micro-benchmark/soak stability, not for
timing-bound cross-thread runs.

`--coverage` (optionally with `--json`) reads the csproj Compile Includes and reports
exactly which real runtime files the harness compiles vs. which stay out of scope —
currently 57/80 (`Coverage` suite in the JSON doc lists every gap as a failed entry, so
a "ready" claim stays measurable). The gates that decide production readiness live in
`NEXUS_READY.md` at the repo root.

Exit code is `0` when everything passes, `1` otherwise.

### Profiling the harness run (.NET 10 tooling)

The run header prints reproducible environment info (runtime version, OS description,
architecture, core count, serverGC flag). For deeper analysis, the standard .NET 10
tooling works against the harness directly:

```powershell
dotnet tool install -g dotnet-trace dotnet-counters dotnet-gcdump   # once
dotnet run -c Release &                                             # start the run in another terminal
dotnet-counters monitor -p <pid> --counters System.Runtime          # live GC/allocation counters
dotnet-gcdump collect -p <pid>                                      # heap snapshot for leak analysis
dotnet-trace collect -p <pid> --profile gc-verbose                  # allocation-sampled trace
```

## What is measured

| Suite | Covers |
| --- | --- |
| Benchmarks (`Program.cs`) | 1000-dispatch timing (<50ms), 1000-subscriber delivery, command pool reuse, `CommandPoolManager` Get/Return zero-alloc regression (<=128 B / 5000 ops), steady-state zero-GC (<=128 B / 5000 fires), 50k-dispatch stress (<800ms), hot-path ns/dispatch (<25us, <30us with subscriber), FSM transition latency (<50us), HybridQueue thread-safe zero-GC, netcode rollback/replay zero-GC, ErrorCollection 4-thread stress (<1s) |
| Recovery regression (`RecoveryRegression.cs`) | Restored sync error-handler tail: generic-only fallback dispatch, retry counting, async-only fallback rejection (no recursion), no-strategy fall-through to Skip |
| Architecture stress (`FullArchitectureStressSuite.cs`) | 39 tests. 1–20: NexusDI resolve+inject, 3-level reentrant zero-GC dispatch, 1000-subscriber fan-out, cross-context routing zero-GC, multi-type pool round-trip zero-GC, 10k FSM transitions, 8-worker HybridQueue stress, plugin interceptor/decorator pipeline (counts asserted: intercepts == decorates == fired == dispatches), high-jitter rollback zero-GC, concurrent ErrorCollection+PerfMonitor, composite trigger (A+B) correctness, lazy-injection resolve-once zero-GC, subscribe/unsubscribe cleanup, HybridQueue next-frame zero-GC, netcode rollback+resimulate state restore, async fire delivery, ObservableProperty zero-GC, SecureObservable write integrity, BigDouble arithmetic, TickService dispatch zero-GC (10k frames × 300 tickables). 21–37 run against the **real runtime** (real `Context`, `Root`, `ContextFactory`, `NexusRuntime`, `ViewBinder`, `SignalBus`, `ContextBuilder`, `NetworkMonitor`, `PluginTraceSink`, storage, pooling, economy): full context lifecycle phase order incl. deferred lazy-service init, assembly-scan auto-registration of `[SignalHandler]`/`[CompositeSignalHandler]` commands, Root parent-child + sibling priority initialization order (via a frame pump), View→ViewRegistration→Context→ViewBinder→mediator bind/unbind/pool-reuse end-to-end (incl. post-unregister silence and re-bind redelivery), NexusRuntime registry/scope lookup/metrics/trace-ring cap, NetworkMonitor event latency + pruning + counts semantics, plugin trace sink auth, AES-256+HMAC encrypted storage, save throttler/offline time/game save, ObjectPoolService spawn/despawn reuse, economy/progression persistence, `ContextBuilder.Validate` strict-injection diagnostics, async ordering (priority order + no-overlap sequential guarantee under an awaited gate), `FireAsyncWithTimeout` cancellation (OCE thrown, command cancelled, bus survives), subscription auto-dispose via context lifetime token, double-dispose idempotence + fire-after-dispose no-op safety, and dispose-during-dispatch (command disposes its own context mid-fire without corrupting the dispatch loop) |

| Fuzz (`FuzzSuite.cs`) | 2 tests against the **real runtime** (`ContextFactory.Create` contexts, real `SignalBus`, `ObjectPoolService`). F1: deterministic xorshift64 fuzz (3 seeds × 10k ops: subscribe/unsubscribe/fire across 8 tags × 5 signal types) with an exact per-tag/per-type delivery model verified after every op, payload integrity (every subscriber of a type sees the most recent fire's payload), plus a zero-GC proof: 20000 fires on a real context with a real registered command — 0 bytes allocated. F2: real object-pool fuzz (10k spawn/despawn ops, 3 prefabs): identity reuse (registry must NOT grow when an inactive instance is available, MUST grow when the stack is empty), balance (instances == created), no-leak teardown |
| Cross-context/thread (`CrossThreadSuite.cs`) | 2 tests against the **real runtime**. C1: 4 real contexts (Gameplay/Gameplay/UI/Net scopes): `[CrossContext(ScopeTag=...)]` scope routing (local delivery + scope-matched broadcast), no-scope `[CrossContext]` broadcast to all, queue→cross-context chain via real `HybridQueue.EnqueueThreadSafe`/`DrainThreadSafe`, disposal silencing, exact registry counts. C2: 16k queued signals from 8 producer threads (per-producer FIFO preserved, delivered == unique == 16k, queue drains to zero), 8 worker contexts owned by 8 threads with per-owner traffic while 4 producer threads enqueue, concurrent context create/dispose, clean teardown (active == 0); async drain via `SubscribeAsync` (100/100 delivered) |
| Dogfood session (`GameSessionSuite.cs`) | 8 tests against the **real runtime**: a full hyper-casual session across 4 context boots — GS1 real Context lifecycle phase order (configure→init→start), GS2 120-frame tick loop + passive income + earn/spend/level-up commands + pool reuse, GS3 SaveThrottler (immediate/throttled/forced) + checkpoint write, GS4 session continuity (next boot loads the checkpoint), GS5 offline income (2h → 7200s → exactly 72000 gold), GS6 crash without save (500 gold lost in memory, kill no-throw), GS7 recovery (last good checkpoint intact, economy resynced from checkpoint — the suite proved this resync is REQUIRED because `EconomyService` persists every Earn to prefs immediately, so crash rollback must come from the game checkpoint), GS8 zero leaks (activeContexts == 0 after 4 boots) |
| Coverage (`CoverageReport.cs`) | Static report: every csproj Compile Include (incl. MSBuild `**` zero-dir globs) expanded against the runtime tree — compiled vs out-of-scope file list, captured as a `Coverage` suite in `--json` output (currently 57/80 compiled) |

The `PerformanceTests.cs` mapping is exact: the harness asserts the same limits
(`<=128` bytes, `<50ms`, `<800ms`, `<25000ns`, `<30000ns`) against the same
operations, plus the pool zero-alloc regression.

## Coverage notes (known non-zero-GC paths)

- **Composite trigger** (`CompositeTriggerState`) is NOT allocation-free by design:
  `CapturePayload` boxes value-type payloads and `SnapshotPayloads()` copies an
  array per completed trigger. Stress test 11 asserts correctness only and reports
  the allocation (~304 B/trigger cycle) as an informational metric.

## Runtime fixes found by the harness

- **Lazy injection was broken**: `NexusDI` created lazy fields via
  `Activator.CreateInstance(f.Type, _di)`, which binds public constructors only,
  while `LazyInjection<T>`'s constructor is `internal` — every lazy-field injection
  threw `MissingMethodException`. Fixed with an explicit non-public binding in
  `Nexus/Packages/com.nexus.core/Runtime/Core/NexusDI.cs` (also un-breaks the Unity
  test `LazyService_ResolvedDuringOnStartAsync_IsInitialized`).
- **Async dispatch permanently leaked the reentrancy counter**:
  `SignalBus.s_stackDepth` is `[ThreadStatic]` and was incremented on the caller's
  thread by `FireInternalAsync`, but after an `await` the continuation — including
  the `finally` decrement — runs on a thread-pool thread, i.e. a *different* slot.
  Every suspended async dispatch leaked +1 on the caller's slot and pushed
  continuation slots negative. Soak mode's cache probe caught it: +2 per stress
  suite run (tests 33/34 suspend exactly one dispatch each), never regressing, until
  the caller's slot exceeded `MaxStackDepth` (10) and the release branch silently
  aborted EVERY dispatch on EVERY bus (log + return) — the whole signal system died
  after ~5 soak iterations. Fixed in
  `Nexus/Packages/com.nexus.core/Runtime/Core/SignalBus.cs`: the async path now
  tracks depth via a static `AsyncLocal<int>` (`s_asyncStackDepth`), which flows
  with the logical chain across threads, so increments/decrements always land on the
  same slot, async recursion is actually detected across threads (it previously
  reset to ~0 on every thread hop and was never caught), and concurrent
  queued/rollback dispatches cannot corrupt each other. The sync fast path keeps the
  thread-static counter. Soak now runs 10/10 clean with `s_stackDepth` flat.

## How it works

`NexusBenchmark.csproj` compiles the real runtime sources (SignalBus, NexusDI,
CommandPool, HybridQueue, PluginSystem, Recovery, FSM, Netcode, ObservableProperty,
SecureObservableProperty, BigDouble, NexusService, TickService, Context, ContextFactory,
ContextBuilder, NexusRuntime, Root, ViewBinder, ViewRegistration, Mediator,
SignalSubscriptions, SignalDispatchPipeline, ContextLifecycleOrchestrator,
NetworkMonitor, PluginTraceSink, storage/encryption, object
pooling, economy/progression, and `Tracing/CausalTracing.cs`) directly from
`Nexus/Packages/com.nexus.core/Runtime/` — 57 of the package's 80 runtime files.
`--coverage` shows the exact list.

One stub file keeps that compiling outside Unity:

- `UnityStubs.cs` — functional `UnityEngine.*` surface: a component/Transform scene
  graph with parent/child hierarchy, `Object.Destroy` (cascades to components and
  descendants, matching Unity semantics), `FindObjectsByType`, and `AddComponent`.

`NexusRuntime`, `Context`/`ContextFactory`, `Root` and the plugin machinery are the
real runtime files (no stand-ins) — that is the whole point: the harness must never
drift from what Unity compiles.

Do not stub runtime types here — compile the real file instead (see
`Tracing/CausalTracing.cs`). Stubs drift; real sources cannot.

## Unity-only exceptions (NOT covered by this harness)

The following real runtime paths are excluded from the harness and remain covered
only by Unity editor tests:

- **`Root.Start()` async main-thread mechanics**: the harness invokes `Awake`/`Start`/
  `OnDestroy` directly and pumps a `TestSyncContext` (one continuation per frame) to
  stand in for Unity's frame loop; it does not run the real Unity message pipeline.
- **Editor-only code**: `OnValidate` (UNITY_EDITOR), `UnityEditor.*` references, and
  `#if UNITY_EDITOR || DEVELOPMENT_BUILD` warning branches in SignalBus.
- **`NEXUS_DEBUG` compiled-out tracing**: `PluginTraceSink.BeginEvent` asserts 0
  without the define (test 27) — the ring-buffer path itself only exists under the
  define and is not exercised.
- **Unity-specific runtime features**: `ScriptableObject`-based configs,
  `[RuntimeInitializeOnLoadMethod]`, `GameObject` asset/prefab instantiation,
  coroutines, and `UnityEngine.Object` native-object lifetime (stub `Destroy` is
  synchronous, not end-of-frame).

## Gotchas

- Stress test 7 (HybridQueue, 8 workers) counts drained items via the `TotalDrained`
  delta, not via a pre-drain depth read: reading the depth then draining races with
  concurrent enqueues and can undercount, leaving the loop spinning forever. The loop
  also carries a 30s watchdog so a stall surfaces as a FAIL, never a hang.
- Stress tests 2 and 4 assert exact execution counts (`==`), not `>=` — a doubled
  dispatch is a failure, not a pass.
- Tests 21+ require a `SynchronizationContext` on the calling thread (Root's
  `async void Start()` posts continuations to it); each test installs and restores
  its own `TestSyncContext` in a `finally`.
- `Task.Yield()`-based waits (Root parent/sibling timeouts) only consume real frames
  because the stub pump executes exactly one queued continuation per call; draining
  the whole queue per call would burn the 900-frame timeouts in milliseconds.
- Test 24 asserts `Received == 0` after unregister+fire: pool-return resets the
  mediator (`ClearInjectedReferences → IResettable.Reset`), so "removed" is proven by
  the counter staying at its reset value, not by comparing to the pre-unregister count.
- Soak mode baselines at iteration 2 (iteration 1 is warmup) and fails on: managed
  heap growth >2MB, working-set growth >12MB, **committed-memory growth >32MB**
  (`GC.GetGCMemoryInfo().TotalCommittedBytes` — pages the GC reserves but does not
  return to the OS; the failure mode working-set deltas miss on long sessions),
  >12 new process threads (C2 spawns ~20 short-lived threads per run; the OS can lag
  reaping by a few), >4 new ThreadPool threads, or growth in any static cache probed
  via reflection (`SignalBus` setter/generic-dispatch/cross-context/list caches +
  `s_stackDepth`, `SubscriptionNodePool`, `NexusRuntime.s_activeContexts`,
  `Root.s_allRoots`, `UnityEngine.Object.s_all`, `NetworkMonitor.s_events`). Gen0/1/2
  collection counts are REPORTED but not gated: the workload legitimately churns ~33
  gen2 collections per iteration (test 11's composite path boxes payloads into
  LOH-sized buffers by design), so counts grow steadily while heap and committed
  memory plateau — gating on counts would false-positive. Tests must be repeat-run
  safe: test 26 seeds `UpdateConnectionStatus(false)` (ClearHistory resets history,
  not live status) and tests 25/30 destroy caller-owned `ContextData`/prefab in
  `finally` blocks (the runtime only destroys objects it spawned).
- Async tests 33/34 carry hard 5s watchdogs (`Task.WhenAny` with `Task.Delay`) so a
  deadlocked async path surfaces as a FAIL, never a hang.
