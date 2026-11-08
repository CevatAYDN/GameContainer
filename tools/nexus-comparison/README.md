# Reproduce the core DI comparison

From the repository root in PowerShell:

```powershell
./tools/nexus-comparison/run.ps1 -Dotnet dotnet
```

Requires Git and .NET 10 SDK. `-Dotnet` accepts a full SDK executable path.
The script creates isolated sparse clones, pins VContainer 1.19.0, Zenject 9.2.0 and
Reflex 14.4.0 by full commit, checks tracked source modifications, then writes JSON and stdout.
It refuses to silently change an existing checkout at a different revision.
Third-party sources and build products are ignored; the libraries are not edited.

The project files list excluded Unity-specific bridges/editor files; Zenject uses
its release's shipped Usage DLL. Reflex compiles with `ENABLE_MONO` (what a Unity Mono
player defines, so it uses its expression-tree activator) and `ReflexHostShims.cs`, which
supplies the few `UnityEngine` members its core files reference (`Assert`, `ListPool<T>`,
`Application.dataPath`, JetBrains annotations) and a silent `ReflexLogger`; scene and
GameObject injectors, `ContainerScope` and the `ReflexSettings` asset are excluded. This compares **core DI on .NET**, using real Nexus
sources via the existing host harness. Unity API stubs supply compile-only dependencies;
Unity scene APIs are not exercised. By default no generator or reflection baking is enabled
for any candidate (`-Generated` below changes that). This is not Unity Mono, IL2CPP, device, full Context startup, or frame
budget evidence. Zenject's core DiContainer has no disposal surface; the common
wrapper includes available disposal operations for Nexus/VContainer/Reflex in build samples.
Reflex's `Container.Dispose` logs at Info level; the host shim logger is silent and the
Unity project sets `ReflexSettings.LogLevel = Warning`, so no candidate pays for logging.

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
Mono support (`-Android` also needs Android build support and `adb` with one device). It creates an isolated project under `Nexus/artifacts/performance-20261007`,
imports the current Nexus package through UPM, copies the pinned VContainer/Zenject
netstandard2.1 DLLs and adds the pinned Reflex source as a UPM `file:` package (Unity
compiles it as a real project would; the build script creates `Resources/ReflexSettings.asset`
with `LogLevel = Warning`, which Reflex requires at runtime). `-ArtifactFolder` selects the
evidence root under `Nexus/artifacts/` (default `performance-20261007`). The **same ComparativeBenchmarks.cs** runs in a non-development,
headless Mono player with Nexus debug tracing disabled. No CI or Actions are used.
`-PackagePath` can select a separate source snapshot for before/after measurements.

In the default (reflection) configuration, generated Nexus binder/factory caches are
cleared at runtime before warmup; this also covers Unity's automatic binder generation, and
the graph uses no codegen/baking for any candidate. The timed register/build/first-resolve workload includes common wrappers
and available disposal; Zenject has no disposal action.

The allocation counter must pass its 4096-byte positive control. On Unity Mono where
it is inert, JSON explicitly records `AllocationCounterValid=false`, the observed
control value, and `BytesPerOperation=-1` (unavailable). This is never a zero-GC result.
The host path still fails if calibration fails. Unity GC.Alloc recorder tests provide
separate event-count evidence; this player tool measures latency, not frame budgets.

Each build run preserves JSON, build/player logs and NexusDI SHA-256; changes to
that source file through the Mono stage are rejected. This is not a complete
immutable build-input manifest. The script redacts CLI access-token arguments in its owned logs.
Existing result JSON is never overwritten; select a new RunName. A run has 12 measurements
(4 libraries × 3 scenarios). Warm metadata core DI
measurements do not establish full Context startup, cold reflection cost, input/scene
integration or a universal ranking.

## Generated bindings and the Android device

- `-Generated` (both scripts): the host build compiles the Nexus source generator's binder and
  VContainer's source generator (built from the pinned checkout); the Unity project gets
  VContainer's generator as an analyzer and runs the editor-generated Nexus binder. Zenject and
  Reflex keep their reflection paths. The JSON records `Generated = true` and its `Scope` says
  which libraries use generated code. Use a separate `-ArtifactFolder`: the analyzer and the
  define stay in that project.
- `-Android` (run-unity.ps1): after the host and Mono runs, the same benchmark is built as an
  Android ARM64 IL2CPP non-development player, installed and run on the connected device;
  `android-results.json`, `android-device.json` (model, Android release, ABI, APK hash) and
  `android-logcat.log` land in the run folder. `-AdbSerial` picks the device when more than one
  is connected. Device numbers drift between launches; compare candidates within one run.
- `-Apk <path>` (run-unity.ps1): installs and runs an APK that an earlier `-Android` run built
  (`<run>/player-android/Comparison.apk`) into a new run folder, with no host run and no build;
  `android-device.json` records `launchOnly`. Launches of several builds can then be interleaved, so
  drift between launches is told apart from a difference between builds (#111). Pass `-Generated`
  with a generated build's APK.

## Checked Android evidence

The Android path freezes the input APK and requires `aapt` to identify exactly
`com.nexus.comparison` before any installation (#343). `-Aapt` selects the executable;
otherwise it uses the pinned editor's Android build tools. Installation uses `-r`.
An already-running comparison app is refused, not automatically stopped. Select a
new evidence folder even after a failed run; `-ArtifactFolder` must stay under this
repository's `Nexus/artifacts`.

Every ADB call records serial, arguments, UTC interval, native exit and output in
`adb-commands.jsonl`. Native and evidence-write failures abort the run. Before
launch the previous benchmark result must be absent and log clearing must succeed.
The launcher observes a new PID and accepts only its exact completion marker and
canonical result path. Resolving the existing storage root also supports a first
install whose result directory does not yet exist.

`android-device.json` links the frozen APK to actual installed APK hashes before
launch and after owned-process termination, the serial/PID, completion marker and
result hash. `android-termination.json` and the command log retain the native
process-end evidence. The run token identifies the **host transcript only**; the
unchanged player does not emit it. Freshness follows the checked file-absence,
new-process and installed-hash chain, not a player-emitted nonce.

`android-results.json` is promoted only after the checked chain and owned-log
finalization succeed. A failed run may retain raw/candidate evidence but is not
accepted. Only the comparison's fixed result file is removed; no game save or
whole app-data reset/uninstall is performed.

Device-free regressions, including failed clear/start, stale result/marker,
wrong-package and post-run hash mismatch:

```powershell
pwsh -NoProfile -File tools/nexus-comparison/test_device_evidence.ps1
```
