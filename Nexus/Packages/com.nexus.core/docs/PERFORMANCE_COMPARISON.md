# Measured core DI comparison — 15 October 2026

Workload measurements of four real DI cores on the same three-object readonly
constructor graph. They are not a universal ranking, not device, not frame-budget and not
full `Context` startup evidence. Earlier snapshots (7 October 2026, three candidates; the
first 15 October run before the DI optimisation) are archived in the repository under
`docs/archive/` and `Nexus/artifacts/comparison-20261015/reflex-mono-001/`; numbers here
replace them.

## Candidates

| Library | Version | Pinned commit | Host build |
|---|---|---|---|
| Nexus | package at repository commit `5fe1aef` plus the DI optimisation in the working tree (see changelog, "Performance") | — | real sources through the host harness (Unity APIs stubbed) |
| VContainer | 1.19.0 | `5401e5a7ebc4980a2b82141ffc26391a6547edd7` | `Runtime/` without `Runtime/Unity`, `NOT_UNITY3D` |
| Zenject | 9.2.0 | `c2e33500a84f9408a809deca2af2c55494ab2482` | `Source/` without Unity bridges, shipped `Zenject-usage.dll` |
| Reflex | 14.4.0 | `70c9b4e46a3b08ee8a13a61b71bb264f14446a7c` | `Assets/Reflex/` without scene/GameObject injectors, `ContainerScope`, settings asset and Unity logger; `ENABLE_MONO` defined (expression-tree activator, as in a Mono player); `ReflexHostShims.cs` supplies `Assert`, `ListPool<T>`, `Application.dataPath`, JetBrains annotations and a silent logger |

No candidate uses source generation or reflection baking; Nexus's generated
binder/factory caches are cleared before warmup. Lifetimes are lazy singletons or
transients; resolved values and lifetimes are asserted. Each scenario warms up 4,000
times and takes seven rounds with rotated candidate order: cached resolve 200,000
operations per round, transient graph 20,000, register/build/first resolve 1,000.
The build workload includes common wrappers and available disposal: Zenject's core
has no disposal action; Reflex's `Container.Dispose` logs at Info level, which is
silenced on host (shim) and in the player (`ReflexSettings.LogLevel = Warning`) so
no candidate pays for logging.

## Host (.NET 10.0.9, Windows 11 x64, Release)

Tiered compilation and server GC disabled; calibrated allocation counter
(positive control passed). Run `after-mono-android-002/host-results.json`.

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Cached resolve, ns/op | **11.23** | 35.44 | 231.52 | 45.22 |
| Transient three-node graph, ns/op | 227.97 | **212.78** | 1265.38 | 286.91 |
| Transient three-node graph, B/op | 72 | 72 | 432 | 72 |
| Register/build/first resolve (warm metadata), ns/op | **1079.40** | 2751.60 | 4648.20 | 1142.70 |
| Register/build/first resolve, B/op | **1768** | 5920 | 6216 | 2056 |

## Unity 6000.5.6f1 Mono player (Windows x64)

Non-development headless player, `NEXUS_DEBUG` off, same benchmark source. Reflex is
compiled by Unity from the pinned source as a UPM package, with the `ReflexSettings`
asset it requires created by the build script. The allocation positive control
returns 0 bytes in this Mono player, so bytes are unavailable (`-1` in JSON); this is
never a zero-allocation claim. Run `after-mono-android-002/unity-results.json`.

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Cached resolve, ns/op | **17.18** | 49.44 | 573.25 | 65.58 |
| Transient three-node graph, ns/op | **514.88** | 1629.58 | 5781.59 | 579.91 |
| Register/build/first resolve (warm metadata), ns/op | **5048.20** | 17202.10 | 26347.40 | 5367.20 |

Before the DI optimisation of 15 October the same player measured Nexus at 1115.55 ns
(transient) and 13353.40 ns (register/build/first resolve); Reflex led both by about 2×.

## Android ARM64 IL2CPP

`run-unity.ps1 -Android` builds the same benchmark as a non-development Android IL2CPP
player (`player-android/Comparison.apk`, built on 2026-10-15, 26 MB) and runs it on the
connected device through adb. The build succeeded; the install was refused by the phone
(`INSTALL_FAILED_USER_RESTRICTED`: USB installation is disabled in its developer
options), so **no device numbers exist yet**. On IL2CPP every candidate constructs
through reflection in this configuration (Nexus and VContainer have no generated
bindings active; Reflex uses its `IL2CPPActivatorFactory`).

## Reading

- Nexus leads cached resolution on both runtimes (about 3× VContainer and 4× Reflex).
- In the Unity Mono player Nexus now leads transient construction (11% ahead of Reflex,
  3.2× VContainer) and warm register/build/first resolve (6% ahead of Reflex, 3.4×
  VContainer). On host VContainer stays 7% ahead on transient construction and Nexus
  leads startup latency and allocation.
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
```

The scripts pin the competitor commits, refuse modified checkouts, never overwrite
existing result JSON and record the Nexus source identity. Evidence (local,
git-ignored): `Nexus/artifacts/comparison-20261015/after-mono-android-002/`. Official releases:
[VContainer 1.19.0](https://github.com/hadashiA/VContainer/releases/tag/1.19.0),
[Zenject 9.2.0](https://github.com/modesttree/Zenject/releases/tag/9.2.0),
[Reflex 14.4.0](https://github.com/gustavopsantos/Reflex/releases/tag/14.4.0).

## Limits

Warm metadata only; cold reflection and code generation, full `Context`
validation/service startup, scenes and providers, generated bindings for every
candidate, Windows IL2CPP (needs the Visual Studio C++ toolchain, not installed on the
measuring machine), device numbers and frame p95/p99 are outside this evidence. Do not
compare isolated figures across machines.
