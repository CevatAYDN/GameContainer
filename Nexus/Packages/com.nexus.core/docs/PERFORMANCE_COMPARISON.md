# Measured core DI comparison — 7 October 2026

## Historical host baseline

These are workload measurements, not a universal ranking. Host: Windows x64, .NET
10.0.12 Release; tiered compilation and server GC disabled. The first table is the historical pre-optimization source baseline,
VContainer 1.19.0 (`5401e5a7ebc4980a2b82141ffc26391a6547edd7`), Zenject 9.2.0
(`c2e33500a84f9408a809deca2af2c55494ab2482`). All three run real core DI code;
Unity bridges/editor paths are excluded and no candidate uses source generation or
reflection baking. This does not measure Unity Mono/IL2CPP, Android/PC frames, editor
UX, or complete Context startup/validation.

| Workload | Library | Median ns/op | B/op |
|---|---|---:|---:|
| Cached reference | Nexus | 10.51 | 0 |
| Cached reference | VContainer | 35.42 | 0 |
| Cached reference | Zenject | 241.65 | 0 |
| Transient three-node graph | Nexus | 587.97 | 72 |
| Transient three-node graph | VContainer | 222.45 | 72 |
| Transient three-node graph | Zenject | 1301.33 | 432 |
| Register/build/first resolve (warm metadata) | Nexus | 4079.30 | 10984 |
| Register/build/first resolve (warm metadata) | VContainer | 3241.70 | 5920 |
| Register/build/first resolve (warm metadata) | Zenject | 4960.60 | 6216 |

Nexus leads cached resolution in this run. VContainer leads transient construction
and register/build/first resolve, and allocates less during build. Transient graph
allocation is 72 B for Nexus/VContainer (the three objects themselves), versus 432 B
for Zenject. The earlier Nexus reflection path measured 160 B for the same graph;
compiled suitable constructors remove 88 B (55%), without a demonstrated latency
improvement. Singleton caches and prewarm do not make transient object creation
allocation-free. Native Unity allocation tests separately assert 3,000 GC.Alloc
events for 1,000 warmed three-object graphs, using a positive-control recorder.

The same readonly constructor graph and value/lifetime assertions are used for all.
Seven measured rounds rotate order after 4,000 warmups. Counts per round: cached
200,000; transient 20,000; build 1,000. JSON contains every round, median/min/max and
calibrated bytes. Build includes common wrappers/delegates and available container
disposal; Zenject core DiContainer exposes no disposal operation. Metadata is warm;
cold reflection/code generation and full startup need separate scenarios.

From the **repository root**, reproduce with Git + .NET 10 SDK:

```powershell
./tools/nexus-comparison/run.ps1 -Dotnet dotnet
```

The script pins official source commits, refuses mismatching/modified checkouts,
and writes local JSON. Project files list exact exclusions. The harness and script
are development tools; Unity consumers do not need .NET installed to use Nexus.
Retain the JSON with your source revision/diff and use a quiet host. This run's JSON:
`Nexus/artifacts/excellence-20261007/comparative-reproducible-results.json`.

Official releases: [VContainer 1.19.0](https://github.com/hadashiA/VContainer/releases/tag/1.19.0),
[Zenject 9.2.0](https://github.com/modesttree/Zenject/releases/tag/9.2.0).
Target-device comparisons with generated bindings and common Unity lifecycle/scene
workloads remain necessary before claiming competitive device performance.


## Current optional-map optimization

Named binding, cross-boundary and lazy-service maps are allocated on first
registration, with synchronized publication and unchanged dictionary concurrency.
In the host three-node register/build/resolve/dispose workload, Nexus allocation
fell from 10984 to 5768 B/op (47.49%). This is an unused-feature cost reduction,
not an allocation-free startup or a new lifetime policy. Features still allocate
when activated. Transient object allocation remains 72 B/op on this host.

Current host .NET 10.0.12 Release results:

| Workload | Library | Median ns/op | B/op |
|---|---|---:|---:|
| cached-resolve | Nexus | 10.48 | 0 |
| cached-resolve | VContainer | 34.03 | 0 |
| cached-resolve | Zenject | 227.90 | 0 |
| transient-three-node-graph | Nexus | 541.41 | 72 |
| transient-three-node-graph | VContainer | 204.50 | 72 |
| transient-three-node-graph | Zenject | 1247.87 | 432 |
| register-build-first-resolve-warm-metadata | Nexus | 2391.40 | 5768 |
| register-build-first-resolve-warm-metadata | VContainer | 2788.50 | 5920 |
| register-build-first-resolve-warm-metadata | Zenject | 4570.60 | 6216 |

VContainer still leads the host transient workload. No transient latency improvement
is attributed to this optional-map change.

## Real Unity Mono player

Windows x64, Unity 6000.5.6f1, Mono 6.13.0 (Visual Studio built mono); non-development
headless player, NexusDebug=false. All use the same shared benchmark source and
pinned core competitors. Nexus's generated injector/factory/metadata registrations
are cleared before warmup to neutralize automatic Unity binder registration. No
source generation/reflection baking is active for any candidate in measured rounds.

| Workload | Library | Median ns/op | B/op |
|---|---|---:|---:|
| cached-resolve | Nexus | 17.66 | unavailable |
| cached-resolve | VContainer | 48.11 | unavailable |
| cached-resolve | Zenject | 599.71 | unavailable |
| transient-three-node-graph | Nexus | 1132.08 | unavailable |
| transient-three-node-graph | VContainer | 1681.55 | unavailable |
| transient-three-node-graph | Zenject | 5896.43 | unavailable |
| register-build-first-resolve-warm-metadata | Nexus | 12947.30 | unavailable |
| register-build-first-resolve-warm-metadata | VContainer | 17565.60 | unavailable |
| register-build-first-resolve-warm-metadata | Zenject | 24853.40 | unavailable |

The allocation positive control returned 0 bytes in this Mono player. JSON therefore
sets AllocationCounterValid=false, PositiveControlMeasuredBytes=0 and B/op=-1.
Unavailable values are not zero allocation claims. GC.Alloc event-count tests are
separate evidence; host bytes are not presented as Unity bytes.

The git-archived pre-change package at 8d8f79a and current source were built and run
sequentially with the same runner. Nexus's measured startup workload went from
23283.90 to 12947.30 ns/op (44.39% lower in these runs). Other candidates'
times also varied, so this is an observed before/after result, not a device-wide
speedup guarantee. Nexus leads these three Mono workloads in this run; the host
transient result demonstrates why a universal ranking remains unjustified.

Reproduce locally on Windows:

```powershell
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-001
```

Requires Unity CLI and installed Windows Mono editor support. The isolated project
uses the real Nexus package via UPM, not host Unity stubs. The core competitor DLLs
exclude their Unity bridges. Startup includes common wrappers and available disposal
(Nexus/VContainer; Zenject core has no disposal action), and warm metadata. Full
Context validation/service startup, cold metadata, generated-binding comparisons,
Android IL2CPP, scenes/providers and frame p95/p99 remain outside this workload.
No CI is required. Source SHA and raw seven rounds are retained in
Nexus/artifacts/performance-20261007/baseline-fair-mono and current-fair-mono.
Earlier before-mono/after-mono runs had generated Nexus registrations active and
are superseded, not used for this comparison.
