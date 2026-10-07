# Reproduce the core DI comparison

From the repository root in PowerShell:

```powershell
./tools/nexus-comparison/run.ps1 -Dotnet dotnet
```

Requires Git and .NET 10 SDK. `-Dotnet` accepts a full SDK executable path.
The script creates isolated sparse clones, pins VContainer 1.19.0 and Zenject 9.2.0
by full commit, checks tracked source modifications, then writes JSON and stdout.
It refuses to silently change an existing checkout at a different revision.
Third-party sources and build products are ignored; the libraries are not edited.

The project files list excluded Unity-specific bridges/editor files; Zenject uses
its release's shipped Usage DLL. This compares **core DI on .NET**, using real Nexus
sources via the existing host harness. Unity API stubs supply compile-only dependencies;
Unity scene APIs are not exercised. No generator or reflection baking is enabled for
any candidate. This is not Unity Mono, IL2CPP, device, full Context startup, or frame
budget evidence. Zenject's core DiContainer has no disposal surface; the common
wrapper includes available disposal operations for Nexus/VContainer in build samples.

All candidates use the same three-object readonly constructor graph. Lifetimes and
resolved values are asserted. Each scenario warms up 4,000 times and takes seven
rounds with rotated candidate order; cached resolve measures 200,000 operations per
round, transient graphs 20,000, registration/build/first resolve 1,000. The final
scenario has warm type metadata and includes common wrapper/delegate allocations.
The calibrated allocation counter has a positive control; JSON reports every round,
median/min/max nanoseconds and bytes per operation. Run with a quiet host and retain
JSON with the Nexus source revision/diff. Do not compare isolated figures across
machines or claim a universal winner from these workloads.

## Local Unity Mono player

```powershell
./tools/nexus-comparison/run-unity.ps1 -Dotnet dotnet -RunName local-001
```

Windows only: requires Unity CLI, the Nexus project's installed editor and Windows
Mono support. It creates an isolated project under `Nexus/artifacts/performance-20261007`,
imports the current Nexus package through UPM and copies the pinned core competitors'
netstandard2.1 DLLs. The **same ComparativeBenchmarks.cs** runs in a non-development,
headless Mono player with Nexus debug tracing disabled. No CI or Actions are used.
`-PackagePath` can select a separate source snapshot for before/after measurements.

Generated Nexus binder/factory caches are cleared at runtime before warmup; this also
covers Unity's automatic binder generation. The graph uses no codegen/baking for any
candidate. The timed register/build/first-resolve workload includes common wrappers
and available disposal; Zenject has no disposal action.

The allocation counter must pass its 4096-byte positive control. On Unity Mono where
it is inert, JSON explicitly records `AllocationCounterValid=false`, the observed
control value, and `BytesPerOperation=-1` (unavailable). This is never a zero-GC result.
The host path still fails if calibration fails. Unity GC.Alloc recorder tests provide
separate event-count evidence; this player tool measures latency, not frame budgets.

Each run preserves JSON, build/player logs and NexusDI SHA-256; source changes during
the run are rejected. The script redacts CLI access-token arguments in its owned logs.
Existing result JSON is never overwritten; select a new RunName. Warm metadata core DI
measurements do not establish full Context startup, cold reflection cost, input/scene
integration, Android IL2CPP performance or a universal ranking.
