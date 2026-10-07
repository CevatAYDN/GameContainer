# Measured core DI comparison — 7 October 2026

These are workload measurements, not a universal ranking. Host: Windows x64, .NET
10.0.12 Release; tiered compilation and server GC disabled. Nexus uses current source,
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
