# Measured core DI comparison — 15 October 2026

Workload measurements of four real DI cores on the same three-object readonly
constructor graph. They are not a universal ranking, not Unity IL2CPP, not device,
not frame-budget and not full `Context` startup evidence. Earlier snapshots (7 October
2026, three candidates) are archived in the repository under
`docs/archive/NEXUS_PERFORMANCE_2026_10_07.md`; numbers here replace them.

## Candidates

| Library | Version | Pinned commit | Host build |
|---|---|---|---|
| Nexus | package at repository commit `9a7001d` plus the comparison-tool changes | — | real sources through the host harness (Unity APIs stubbed) |
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
(positive control passed).

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Cached resolve, ns/op | **10.45** | 35.62 | 230.73 | 45.49 |
| Transient three-node graph, ns/op | 554.01 | **211.09** | 1282.14 | 288.09 |
| Transient three-node graph, B/op | 72 | 72 | 432 | 72 |
| Register/build/first resolve (warm metadata), ns/op | 2493.70 | 2791.20 | 4788.50 | **1210.40** |
| Register/build/first resolve, B/op | 5768 | 5920 | 6216 | **2056** |

## Unity 6000.5.6f1 Mono player (Windows x64)

Non-development headless player, `NEXUS_DEBUG` off, same benchmark source. Reflex is
compiled by Unity from the pinned source as a UPM package, with the `ReflexSettings`
asset it requires created by the build script. The allocation positive control
returns 0 bytes in this Mono player, so bytes are unavailable (`-1` in JSON); this is
never a zero-allocation claim.

| Workload | Nexus | VContainer | Zenject | Reflex |
|---|---:|---:|---:|---:|
| Cached resolve, ns/op | **17.55** | 49.05 | 606.62 | 66.16 |
| Transient three-node graph, ns/op | 1115.55 | 1623.64 | 5706.61 | **587.82** |
| Register/build/first resolve (warm metadata), ns/op | 13353.40 | 17703.40 | 25412.00 | **5514.70** |

## Reading

- Nexus leads cached resolution on both runtimes (about 3× VContainer and 4× Reflex).
- Reflex leads transient construction in the player (about 1.9× faster than Nexus) and
  warm register/build/first resolve on both runtimes (2.4× faster than Nexus in the
  player, with 2.8× less allocation on host). VContainer leads transient construction on
  host only.
- Transient object creation allocates the three objects (72 B) for Nexus, VContainer
  and Reflex; singleton caches and prewarm do not change that.
- Closing the transient and startup gap to Reflex is the open performance item in the
  repository's `docs/RELEASE_GATES.md`.

## Reproduce

From the repository root (Git, .NET 10 SDK; the Unity run needs the Unity CLI and the
project's editor with Windows Mono support):

```powershell
./tools/nexus-comparison/run.ps1 -Dotnet dotnet
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-001 -ArtifactFolder comparison-20261015
```

The scripts pin the competitor commits, refuse modified checkouts, never overwrite
existing result JSON and record the Nexus source identity. This run's evidence (local,
git-ignored): `Nexus/artifacts/comparison-20261015/reflex-mono-001/` (`host-results.json`,
`unity-results.json`, `source-identity.json`, build and player logs). Official releases:
[VContainer 1.19.0](https://github.com/hadashiA/VContainer/releases/tag/1.19.0),
[Zenject 9.2.0](https://github.com/modesttree/Zenject/releases/tag/9.2.0),
[Reflex 14.4.0](https://github.com/gustavopsantos/Reflex/releases/tag/14.4.0).

## Limits

Warm metadata only; cold reflection and code generation, full `Context`
validation/service startup, scenes and providers, generated bindings for every
candidate, Windows/Android IL2CPP and frame p95/p99 are outside this workload. Do not
compare isolated figures across machines.
