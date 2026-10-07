# Correctness, integration and performance hardening

Target: Unity 6 + UPM for mobile and PC. Audit: 6 October 2026; implementation validation:
7 October 2026. All R01–R20 have source changes; R15 preserves the immutable legacy
snapshot contract and adds an allocation-free typed alternative. Platform acceptance is separate.

## Verification

Latest source integration/reentrancy update: 7 October 2026, based on commits
`8c91403`, `94beec3`, `21a0b7b`, `3cd154b` and the subsequent working-tree fixes.
Current HEAD/diff and fresh result files take precedence over earlier counts.

| Check | Evidence |
|---|---|
| Existing host harness | 291 checks passed from repository root; Unity APIs are stubbed |
| Real Unity 6000.5.6f1 EditMode | 352 passed / 0 failed / 0 skipped; includes integration metadata, source generation, actual IMGUI/Undo, editor creation and 8 ready/reentrancy cases |
| Real Unity PlayMode | 160 passed / 0 failed; 2 debug-configuration assertions skipped here and checked in a debug-off consumer |
| Storage numeric regressions | 13 native checks passed; concrete Unity prefs and legacy provider invariant round-trip, finite validation, cache promotion/backend ownership |
| Optional Collections/DOTS | 3 native tests use real parallel jobs: drain, reinitialize and destroy complete registered producers; framework tests install Collections 2.6.8 without requiring it for core consumers |
| Main-project consumer readiness | Actual Awake before code-first startup, owning-parent selection, disposed-owner reattachment, serialized global discovery, failed-global replacement and cleanup covered by native PlayMode |
| Clean tarball and platform acceptance | Prior verified archive: real sample import, debug-off allocation checks, Windows Mono player and Android ARM64 IL2CPP build. Fresh archive identity/results are recorded outside the package under artifacts/excellence-20261007; do not transfer the older archive's result to new source |
| Open device/platform evidence | Android/iOS physical runtime, Windows IL2CPP, actual game frame p95/p99, device storage interruption and native competitor comparison remain unverified |

Permanent regression fixtures include IntegrationReadinessTests,
LatestServiceRegressionTests, LatestCommitEditorRegressionTests,
ReadyReentrancyRegressionTests, LatestLifecycleRegressionTests and optional
DotsOwnershipRegressionTests. Framework observer callbacks cannot double-inject a
component or return a disposed owner as successful startup. Readiness observers stop
when an earlier observer disposes the owner. Review is not exhaustive branch coverage.

Core DI comparison and reproducibility: [PERFORMANCE_COMPARISON.md](PERFORMANCE_COMPARISON.md).
Nexus leads cached resolves in the recorded host run; VContainer leads transient
construction and warm register/build startup. No universal or target-device winner
is claimed. The temporary constructor-array allocation was removed on supported
Mono/JIT paths; 72 B remains for the three objects in the host graph.

## Measured host performance

Windows x64, .NET 10.0.12, Release, tiered compilation/server GC disabled, no tracing,
interceptors or decorators. Warmup: 2,000 operations. Each rollback measurement uses
100 sparse synchronous root inputs, no model capture and 200 measured cycles. The
before project substitutes the audited HEAD's netcode source; other harness dependencies
are shared, so this isolates the netcode implementation rather than comparing whole releases.

| Workload | Before µs/op | After µs/op | Before B/op | After B/op |
|---|---:|---:|---:|---:|
| Rollback 1,000 ticks / 100 inputs | 517.644 | 23.977 | 88,088 | 0 |
| Rollback 2,000 ticks / 100 inputs | 1,058.290 | 33.765 | 176,088 | 0 |
| Rollback 4,000 ticks / 100 inputs | 2,050.646 | 52.084 | 352,088 | 0 |

The 4,000-tick scenario is about 39 times faster in this run. This is a microbenchmark,
not a scene frame-time or competitor measurement. Time varies with host load; allocation
is also covered by strict regressions. A separate nested rollback regression measures
0 B over 1,000 warmed cycles. Typed two-signal composites measure **0 B/completion**
over 20,000 completions; legacy immutable composites retain **152 B/completion** on
this host. Startup, registration, growing histories, async, debug and middleware may allocate.

## Audit closure

| ID | Implemented contract |
|---|---|
| R01 | DI cache excludes transient/inherited/adapter entries, checks disposal, invalidates local rebinding |
| R02 | Storage migrations commit only while the read key version remains current |
| R03 | Save completion/retry accounting cannot discard a newer request |
| R04 | Cross-type journal and parent identities preserve nested inline replay order |
| R05 | Reentrant list drains own buffers per instance and release retained payloads |
| R06 | Cancellation callback failures still tear down/unregister; async stop gets an independent token |
| R07 | Late economy reconciliation cannot resurrect disposed balances |
| R08 | Rejected-spend refunds saturate instead of wrapping |
| R09 | Property notifications preserve published old/new pairs and suppress coalesced no-ops |
| R10 | UI close ownership prevents recursive duplicate pooling |
| R11 | UI type identities distinguish same-name types; short display/resource names stay compatible |
| R12 | Level completion stops at int.MaxValue |
| R13 | Progression saves one coherent current/max tuple and normalizes stored ranges |
| R14 | Editor AOT factories accept single public constructors, matching runtime/source-generator contracts |
| R15 | Typed two-signal composites avoid boxing; immutable legacy snapshot semantics retained |
| R16 | Named injection annotations are allowed on constructor/method parameters |
| R17 | Resource cancellation registration is released at completion/failure/cancellation |
| R18 | Replay indexes once; no repeated full-history scans or Values collection allocation per tick |
| R19 | FSM cards retained until state/configuration/history changes; disable clears state and subscriptions |
| R20 | Unsupported replacement keeps a recoverable backup instead of deleting the only committed save |

Storage backup recovery uses controlled process-interruption states in host tests. Filesystem
power-loss guarantees require device evidence. Migration and import both coordinate key
versions with disk commits; latest manual mutations remain dirty until saved.

## Integration and replay contracts

Import **Code-first Startup** from UPM Samples, attach `ScoreBootstrap`, then enter Play Mode.
The sample's exact source compiles against Unity references; its model/command/startup flow
has a host regression. [English integration](GETTING_STARTED.md) / [Türkçe](GETTING_STARTED_TR.md).
The Setup Wizard preserves conflicting existing files/settings/scenes and creates new scenes
additively. Existing Root/discovery APIs remain supported.

Network producers use `Fire` from workers with Nexus SignalBus. Drain the owning context's
HybridQueue before advancing ticks, firing top-level owner inputs or beginning rollback.
Pending inputs are rejected at these boundaries, preserving live versus replay order. Custom
ISignalBus implementations support owner-thread calls; worker transport requires Nexus SignalBus.
Synchronous handlers are required for deterministic snapshots. Async rollback remains explicitly
unsupported for deterministic state. Prune confirmed history to bound memory.

## Native Unity integration corrections

- Immutable UPM archives require committed `.meta` files for every imported source; missing
  metadata made six existing source files disappear in a real clean installation.
- Runtime/AOT catalogs exclude `.testing`, `Assembly-CSharp-Editor` and Unity's editor-only
  `StandardSocketsHttpHandler` implementation. Runtime and source-generator policies agree.
- Command registration preserves local transient factories/constructor overrides. Command pools
  normalize cached bindings and create defaults in their owning scope, preventing pooled DI-owned
  singleton reuse and preserving child constructor dependencies.
- Trace timestamps use `NexusTrace.TimestampNow`, monotonic seconds since the trace clock was
  initialized. They can be captured on workers and must not be compared with Unity's startup clock.
  EndEvent copies frame ownership before returning pooled frames and updates the ring under its lock.
- Real lifecycle tests replace ignored placeholders. Async tests await continuations rather than
  blocking Unity's main thread. Authenticated storage export/import is key and seed bound.

## Allocation evidence

This Unity Mono runtime returned zero from `GC.GetAllocatedBytesForCurrentThread()` even for a
retained 4 KB allocation. Those original Unity zero-GC results are invalid evidence. Allocation
regressions now use the native `GC.Alloc` marker, restricted to the calling thread, with a positive
allocation control before every measurement. Overflow/unavailable counters fail explicitly.
Tests assert zero allocation samples; samples are not reported as byte counts. Debug tracing has
intentional allocation costs, so its zero-allocation assertion requires a separate debug-off run.
The .NET host probe calibrates its byte counter and supplies a zero/nonzero allocation indicator.
See Unity's [thread-scoped recorder example](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread.html).

Competitor superiority has not been established. Editor and clean-consumer evidence is bounded
by the tested configuration. Before release, profile actual mobile/PC games, exercise Android/iOS
on target hardware and verify device storage interruption. An Android IL2CPP build is AOT compilation
evidence, not Android runtime or frame-time acceptance.