# Unity 6000.6.7 Compile Verification (Mono + IL2CPP)

The `.NET` harness in `tools/nexus-benchmark` compiles the real `com.nexus.core`
sources and proves behavior, but it cannot prove the **Unity pipeline** compiles:
editor assemblies, the demo scene, URP shader/graphics settings, and — most
critically — the **IL2CPP/AOT** path (code stripping, `link.xml` preservation,
generic dispatch survival without a JIT).

This folder is the missing half: a repeatable, headless command sequence that
compiles the demo project under Unity **6000.6.7f1** with both scripting
backends.

## Prerequisites

- Unity **6000.6.7f1** (matches `Nexus/ProjectSettings/ProjectVersion.txt`),
  installed via Unity Hub, with the **Windows Build Support** module.
  - Optional: **Android Build Support** (SDK + NDK) if you want the Android
    IL2CPP step.
- The editor must be activated (license) on the machine.

## Quick start

```bash
# from the repo root
UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe" \
  bash tools/unity-verify/verify-unity-build.sh
```

Logs and test result XMLs land in `tools/unity-verify/artifacts/`.

## The four verification steps (manual command list)

### 1. EditMode tests — editor + runtime compile under Mono

```bash
"<Unity 6000.6.7f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -runTests -testPlatform EditMode \
  -testResults "tools/unity-verify/artifacts/editmode-results.xml" \
  -logFile "tools/unity-verify/artifacts/editmode.log" -quit
```

Compiles every editor + runtime assembly and runs the NUnit EditMode suite.
Any `result="Failed"` in the XML is a failure.

### 2. PlayMode tests — runtime boots and dispatches in a player context

```bash
"<Unity 6000.6.7f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -runTests -testPlatform PlayMode \
  -testResults "tools/unity-verify/artifacts/playmode-results.xml" \
  -logFile "tools/unity-verify/artifacts/playmode.log" -quit
```

Boots `NexusStarter` and runs the PlayMode suite (context lifecycle, signal
dispatch, pooling, storage) against the real Unity runtime.

### 3. Mono player build — full player compile (Mono backend)

```bash
"<Unity 6000.6.7f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneMono \
  -logFile "tools/unity-verify/artifacts/mono-build.log" -quit
```

Produces `Nexus/builds/standalone-mono/` (each Windows player builds into its own folder, issue #102).
Every build also writes `Nexus/builds/<name>.commit`: the commit it was built from, marked
"(Packages or Assets modified)" when those folders differed from it (issue #105).
A success proves the whole game
(scripts + scene + URP) compiles into a runnable Mono player.

### 4. IL2CPP player build — code-stripping + AOT verification

```bash
"<Unity 6000.6.7f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneIL2CPP \
  -logFile "tools/unity-verify/artifacts/il2cpp-build.log" -quit
```

Produces `Nexus/builds/standalone-il2cpp/`. It needs Visual Studio's C++ toolchain, which the machine
`docs/RELEASE_GATES.md` measures on has had since 2026-10-23 (Build Tools 2022 with the C++ workload).
This is the strongest signal:
IL2CPP compiles the C# to C++ and strips unused code, so it validates that
- the build succeeds with the packages' `link.xml` files handed to UnityLinker by `NexusPackagesLinkXml`
  (the build log says how many; Unity itself passes the linker only the files under `Assets`, so before
  issue #110 the packages' files were never read). At the project's level, Minimal, the linker copies the
  package assemblies whole: what those files preserve is tested only by the stripped builds below (Low,
  Medium or High). `NexusVerifyBuild` fails an IL2CPP build that did not write
  `Library/Nexus/PackagesLink.xml` (#115),
- the `[Preserve]`-annotated runtime surface survives stripping,
- a public type that exists only in the Editor and development builds
  (`Nexus/Assets/Scripts/Verify/DevOnlyProbe.cs`) is named in the generated binder only under its own
  `#if` (issue #85; the release Mono build checks the same),
- nothing relies on runtime JIT (`Expression.Compile` in `CommandRegistry.cs` and the
  `DynamicMethod` emitter in `CompiledAccessorEmitter.cs`, which `NexusDI` uses, sit behind
  `ENABLE_IL2CPP` / `UNITY_AOT` guards).

### Optional: Android IL2CPP (needs SDK/NDK)

```bash
"<Unity 6000.6.7f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPP \
  -logFile "tools/unity-verify/artifacts/android-il2cpp-build.log" -quit
```

The most realistic mobile pipeline (Android + IL2CPP + stripping).

`NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPPDevelopmentStripped -nexusStripping Low|Medium|High`
(default High) builds the development player below at that managed stripping level
(`Nexus/builds/android-il2cpp-dev-<level>.apk`; issues #110, #113). The projects and the games build at
Minimal, where the linker copies the assemblies whole, so only these builds test what is stripped. After
one, check the assemblies the linker left in `Nexus/Library/Bee/artifacts/Android/ManagedStripped`:

```powershell
pwsh tools/unity-verify/check-stripped.ps1 -Out Nexus/artifacts/stripped-<level>.json
```

It fails when a method that a kept interface declares has lost its implementation on a class of the
Nexus or game assemblies, which IL2CPP reports as `EntryPointNotFoundException` at the call (exit 0
none missing, 1 missing, 2 could not run). It matches by name, generic arity and parameter count, not
parameter types. The packages' assemblies in that folder show what their `link.xml` files kept, and the
probe's `framework services resolved 4/4 injected 4/4` line shows the services still construct and
inject. Unity has no call that unsets a stripping level, so the build writes the previous level back
explicitly; run `git checkout -- Nexus/ProjectSettings/ProjectSettings.asset` after it (this also discards any
other uncommitted change to that file).

`NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPPDevelopment` builds the same player as a development
build (`Nexus/builds/android-il2cpp-dev.apk`) with `Assets/Scripts/Verify/DeviceProbe.cs`, which exists
only there: on the phone it logs `[Probe]` lines (`adb logcat -s Unity`) with the first resolve of the
framework services Gigawatt binds (issue #110), the managed allocations of each haptic play (issue #84) and, after each HOME and return, the time away by the wall clock,
`Stopwatch`, `SystemClock.elapsedRealtime` and `OfflineTimeCalculator` (issue #77). Both Android players
have the same application id, so installing one replaces the other: before a probe run, compare
`adb shell dumpsys package <application id> | grep lastUpdateTime` with the APK's time and look for a
`[Probe]` line after the launch.

`NexusVerify.NexusVerifyBuild.BuildAndroidStackProbe` builds the development player with the define
`NEXUS_STACK_PROBE` (`Nexus/builds/android-il2cpp-stackprobe.apk`), which compiles
`Assets/Scripts/Verify/StackProbe.cs`, the phone check of the screen stack (issue #76, ADR-0003 verification
plan item 4). `pwsh tools/unity-verify/stack-probe.ps1 -AdbSerial <serial>` installs it and launches it once for
each UI input setup of the ADR's decision 7 (the intent extra `nexusInputMode`: `eventsystem`, `eventsystem-once`,
`inputforui`). Each launch sends `KEYCODE_BACK` three times in each of 10 rounds (sheet, modal, root), drags over
the screen for 30 s of a 60 s capture of the managed allocation per frame, then presses back twice, which must quit
the app; a launch passes only when every press reached the expected screen and the drag reached the screen as
pointer events. The evidence goes to `Nexus/artifacts/<RunName>/` (`stack-probe.json`, and `probe-<mode>.txt` and
`logcat-<mode>.txt` for each launch); exit 0 passed, 1 failed, 2 could not run. It has the same application id as
the players above, so it replaces them on the phone.

## Mode B: verify the Roslyn Source Generator binder (optional, opt-in)

The four steps above compile the game with the **editor-time generator's** binder
(`Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs`, written by the **Nexus >
Generate AOT Binder** menu). Since the package now also ships a **Roslyn Source
Generator** (`com.nexus.core/SourceGenerators~/Nexus.SourceGenerator.dll`), the
IL2CPP step can instead verify the source-generator binder. The generator's output
is already proven valid, compiling C# that boots end to end by the harness
(CodeGenSuite CG2, `tools/nexus-benchmark`); Mode B proves it inside a real Unity
6000.6 IL2CPP build.

One-file rule: only one binder may exist per assembly. Mode B removes the editor
file and lets the compiler generate the binder. To switch back, delete the
`csc.rsp` line and regenerate the editor file via the menu.

```bash
# 1) Activate the source generator for the demo's predefined Assembly-CSharp
#    (the demo has no asmdefs; csc.rsp drives predefined assemblies).
#    Create Nexus/Assets/csc.rsp containing:
#
#      -analyzer:Packages/com.nexus.core/SourceGenerators~/Nexus.SourceGenerator.dll
#
# 2) Remove the editor-time binder so the generated class does not collide:
rm Nexus/Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs
#    (the .meta is regenerated by Unity on next import; the file itself is gitignored)

# 3) Run the IL2CPP step — the compile now includes the source-generated binder:
UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe" \
  bash tools/unity-verify/verify-unity-build.sh

# 4) Revert (back to editor-time binder):
rm Nexus/Assets/csc.rsp
#    open Unity and run the Nexus > Generate AOT Binder menu (or run the generator
#    from code), then re-run step 3.
```

## How to interpret a failure

Each step is a separate Unity batch process that exits non-zero on failure and
prints the tail of its log. Common failure classes and where to look:

| Symptom | Log line | Likely cause |
|---|---|---|
| `error CS####` | `mono-build.log` / `il2cpp-build.log` | C# compile error — fix and re-run |
| `Build FAILED ... result=Failed` | build log | Asset/scripting issue at link time |
| `Internal build system has thrown an exception` | build log | Missing build-support module |
| `result="Failed"` in test XML | `editmode-results.xml` | Test assertion failure — inspect `<test-case>` entries |

The `.NET` harness (`tools/nexus-benchmark`) and this Unity pipeline are
complementary: run both before merging runtime changes.
