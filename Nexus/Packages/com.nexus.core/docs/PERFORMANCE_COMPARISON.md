> **Who this is for:** level 3, anyone deciding between DI frameworks on evidence. Pinned, reproducible measurements with their limits.

# Measured core DI comparison — 15 October 2026

Workload measurements of four real DI cores on the same three-object readonly
constructor graph, in two configurations: **reflection** (no candidate has generated
bindings) and **generated** (every candidate that has a generator uses it). They are not a
universal ranking, not frame-budget and not full `Context` startup evidence. Earlier
snapshots (7 October 2026, three candidates; the 15 October runs before the DI optimisation
and before the reset fix) are archived under `docs/archive/` and
`Nexus/artifacts/comparison-20261015/`; numbers here replace them.

## Candidates

| Library | Version | Pinned commit | Host build |
|---|---|---|---|
| Nexus | package sources at the measuring commit (`source-identity.json` in each run records the commit and the working-tree diff) | — | real sources through the host harness (Unity APIs stubbed) |
| VContainer | 1.19.0 | `5401e5a7ebc4980a2b82141ffc26391a6547edd7` | `Runtime/` without `Runtime/Unity`, `NOT_UNITY3D` |
| Zenject | 9.2.0 | `c2e33500a84f9408a809deca2af2c55494ab2482` | `Source/` without Unity bridges, shipped `Zenject-usage.dll` |
| Reflex | 14.4.0 | `70c9b4e46a3b08ee8a13a61b71bb264f14446a7c` | `Assets/Reflex/` without scene/GameObject injectors, `ContainerScope`, settings asset and Unity logger; `ENABLE_MONO` defined (expression-tree activator, as in a Mono player); `ReflexHostShims.cs` supplies `Assert`, `ListPool<T>`, `Application.dataPath`, JetBrains annotations and a silent logger |

In the reflection configuration no candidate uses source generation or reflection baking
(Nexus's generated registrations are removed with `NexusDI.ClearGeneratedRegistrations()`
before warmup). In the generated configuration (`run.ps1 -Generated`, `run-unity.ps1
-Generated`, define `NEXUS_COMPARE_GENERATED`) Nexus runs its generated binder (constructor
factories; the Roslyn generator on host, the editor generator in Unity) and VContainer its
source-generated injectors (`VContainer.SourceGenerator` built from the pinned commit, a
`RoslynAnalyzer` in Unity); the benchmark verifies both at runtime and refuses to run if
either is missing. Reflex's generator covers attribute injection only (no effect on this
constructor graph) and Zenject has none, so both are unchanged between configurations.
Lifetimes are lazy singletons or transients; resolved values and lifetimes are asserted. Each scenario warms up 4,000
times and takes seven rounds with rotated candidate order: cached resolve 200,000
operations per round, transient graph 20,000, register/build/first resolve 1,000.
The build workload includes common wrappers and available disposal: Zenject's core
has no disposal action; Reflex's `Container.Dispose` logs at Info level, which is
silenced on host (shim) and in the player (`ReflexSettings.LogLevel = Warning`) so
no candidate pays for logging.

## Host (.NET 10.0.9, Windows 11 x64, Release)

Tiered compilation and server GC disabled; calibrated allocation counter
(positive control passed). Reflection: run `comparison-20261015/after-reset-android-006/host-results.json`;
generated: `comparison-20261015-generated/generated-android-002/host-results.json`.

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Reflection: cached resolve, ns/op | **10.67** | 34.47 | 232.41 | 45.35 |
| Reflection: transient three-node graph, ns/op | **170.11** | 205.62 | 1326.62 | 284.06 |
| Reflection: register/build/first resolve (warm metadata), ns/op | 1140.50 | 3007.80 | 4870.30 | **1132.60** |
| Generated: cached resolve, ns/op | **10.50** | 36.67 | 244.53 | 44.65 |
| Generated: transient three-node graph, ns/op | 187.35 | **67.72** | 1253.01 | 280.51 |
| Generated: register/build/first resolve (warm metadata), ns/op | 1138.80 | 2568.20 | 4857.50 | **1118.30** |
| Transient three-node graph, B/op (both) | 72 | 72 | 432 | 72 |
| Register/build/first resolve, B/op (both) | **1768** | 5920 / 5968 | 6216 | 2056 |

## Unity 6000.5.6f1 Mono player (Windows x64)

Non-development headless player, `NEXUS_DEBUG` off, same benchmark source. Reflex is
compiled by Unity from the pinned source as a UPM package, with the `ReflexSettings`
asset it requires created by the build script. The allocation positive control
returns 0 bytes in this Mono player, so bytes are unavailable (`-1` in JSON); this is
never a zero-allocation claim. Runs `after-reset-android-006/unity-results.json` (reflection)
and `generated-android-002/unity-results.json` (generated).

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Reflection: cached resolve, ns/op | **17.33** | 50.90 | 625.94 | 62.68 |
| Reflection: transient three-node graph, ns/op | **451.16** | 1642.26 | 5931.61 | 605.43 |
| Reflection: register/build/first resolve (warm metadata), ns/op | **5570.30** | 19297.40 | 28383.00 | 5858.10 |
| Generated: cached resolve, ns/op | **17.20** | 49.37 | 604.02 | 62.82 |
| Generated: transient three-node graph, ns/op | 491.51 | **255.40** | 5921.07 | 607.22 |
| Generated: register/build/first resolve (warm metadata), ns/op | **5526.60** | 17178.80 | 28006.20 | 6171.90 |

Before the DI optimisation of 15 October the same player measured Nexus at 1115.55 ns
(transient) and 13353.40 ns (register/build/first resolve) in run
`comparison-20261015/reflex-mono-001/unity-results.json`; Reflex led both by about 2×.
In a Mono player Nexus already constructs through compiled constructor delegates, so its
generated factories change little there; VContainer's generated injectors do.

## Android ARM64 IL2CPP on a device

`run-unity.ps1 -Android` builds the same benchmark as a non-development Android IL2CPP
player (`player-android/Comparison.apk`, 26 MB) and runs it on the connected device through
adb. Device: Xiaomi 2311DRK48G, Android 16, arm64-v8a; the phone owner
accepted the USB install. Allocation bytes are unavailable in the player (`-1`). Runs
`after-reset-android-006/android-results.json` (reflection) and
`generated-android-002/android-results.json` (generated), each with `android-device.json`
and `android-logcat.log`.

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Reflection: cached resolve, ns/op | **37.09** | 148.11 | 678.18 | 216.04 |
| Reflection: transient three-node graph, ns/op | 5511.81 | **3940.83** | 14794.42 | 4288.48 |
| Reflection: register/build/first resolve (warm metadata), ns/op | 29180.00 | 66132.40 | 99142.60 | **27548.60** |
| Generated: cached resolve, ns/op | **36.50** | 142.29 | 693.30 | 213.23 |
| Generated: transient three-node graph, ns/op | 2195.36 | **1004.29** | 12798.75 | 4007.95 |
| Generated: register/build/first resolve (warm metadata), ns/op | 15410.70 | 42200.10 | 64330.90 | **14567.70** |

Reflection configuration: Nexus builds objects with `ConstructorInfo.Invoke` (its compiled
constructor delegates exist only where IL emission is available), VContainer uses its
reflection injector and Reflex its `IL2CPPActivatorFactory`; Nexus's reflection construction
is 1.4× slower than VContainer's and 1.3× slower than Reflex's on this device.

Generated configuration, the one a shipped IL2CPP game runs: Nexus's generated constructor
factories cut its transient cost by 2.5× (5.5 → 2.2 µs) and VContainer's generated injectors
cut its own by 3.9× (3.9 → 1.0 µs). VContainer's injector constructs the graph with one
generated call per type; Nexus's factory resolves each constructor parameter through the
container's full resolve path (lookup, lifetime, cycle bookkeeping), which is where the
remaining 2.2× comes from and is the next lever. Cached resolution stays ahead in both
configurations: 4.0× / 3.9× VContainer and 5.8× Reflex (reflection / generated).

Device runs drift between launches: Zenject, which has no generator and did not change,
measured 99.1 µs and 64.3 µs startup in the two runs a few minutes apart (thermal and clock
state). Compare candidates within one run; do not read Nexus's 29.2 → 15.4 µs startup as a
generated-bindings gain (Reflex moved 27.5 → 14.6 µs in the same runs). A copy-on-write
binding map (run `after-cow-android-004`) and transient bindings in the generic fast cache
(run `after-fastslot-android-005`: cached resolve 94 ns) were tried on this device and rejected.

After the issue #23 DI fixes (M1: a singleton built while its container is disposed is
disposed; M8: constructor arguments of one type fill their parameters in order; both
Unreleased), two reflection runs on `b423ac4`, `audit23-android-007` and `audit23-android-008`,
measured Nexus at 31.1 / 29.8 ns cached, 5376 / 5200 ns transient and 29521 / 29047 ns startup.
The two runs before the change (`rc-050`, `after-reset-android-006`) span 27.8–37.1 ns,
4890–5512 ns and 12872–29180 ns. Every new value lies within that span except
`audit23-android-007`'s startup, 0.3 µs (1.2%) above its maximum and far less than the drift
between launches: no regression shows on this device. In the two `audit23` runs Reflex's startup was 13% and 10% faster than Nexus's (in `rc-050` Nexus was 11% faster),
the launch-to-launch drift described above.

## Reading

- Nexus leads cached resolution everywhere (2.9–4.0× VContainer, 3.6–5.8× Reflex).
- Reflection configuration: Nexus leads transient construction on host (17% ahead of
  VContainer) and in the Mono player (25% ahead of Reflex, 3.6× VContainer); on the device it
  is last of the three maintained libraries (reflection construction). Startup: Nexus and
  Reflex are within 1% of each other on host, Nexus is 5% faster in the Mono player and Reflex
  6% faster on the device; VContainer is 2.3–3.5× behind.
- Generated configuration: VContainer's generated injectors lead transient construction on
  every runtime (2.8× Nexus on host, 1.9× in the Mono player, 2.2× on the device); Nexus is
  second, ahead of Reflex (1.5× on host, 1.2× in the Mono player, 1.8× on the device).
  Startup: Nexus and Reflex are within 2% of each other on host, Nexus is 10% faster in the
  Mono player and Reflex 5% faster on the device; VContainer is 2.3–3.1× behind.
- Transient object creation allocates the three objects (72 B) for Nexus, VContainer
  and Reflex; singleton caches and prewarm do not change that.

## Reproduce

From the repository root (Git, .NET 10 SDK; the Unity runs need the Unity CLI and the
project's editor with Windows Mono support; `-Android` needs Android Build Support, adb
and one connected device that allows USB installs):

```powershell
./tools/nexus-comparison/run.ps1 -Dotnet dotnet
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-001 -ArtifactFolder comparison-20261015
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-002 -ArtifactFolder comparison-20261015 -Android
./tools/nexus-comparison/run.ps1 -Dotnet dotnet -Generated
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-003 -ArtifactFolder comparison-20261015-generated -Android -Generated
```

The generated configuration uses its own artifact folder because the analyzer label and the
define stay in that Unity project.

The scripts pin the competitor commits, refuse modified checkouts, never overwrite
existing result JSON and record the Nexus source identity. Evidence (local,
git-ignored): `Nexus/artifacts/comparison-20261015/after-reset-android-006/` and
`Nexus/artifacts/comparison-20261015-generated/generated-android-002/`. Official releases:
[VContainer 1.19.0](https://github.com/hadashiA/VContainer/releases/tag/1.19.0),
[Zenject 9.2.0](https://github.com/modesttree/Zenject/releases/tag/9.2.0),
[Reflex 14.4.0](https://github.com/gustavopsantos/Reflex/releases/tag/14.4.0).

## Limits

Warm metadata only; cold reflection and code generation, full `Context`
validation/service startup, scenes and providers, Windows IL2CPP (the comparison tool builds
no Windows IL2CPP player) and frame p95/p99 are outside this evidence; the device numbers come
from one phone model. Do not
compare isolated figures across machines.
