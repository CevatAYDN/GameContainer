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

Runs the PlayMode suite (context lifecycle, signal dispatch, pooling, storage)
against the real Unity runtime. No test loads `NexusStarter`; the player builds
below boot it.

### 2b. PlayMode twice with domain reload off

Unity's "Path to CoreCLR" guide (2026-03-26) makes Fast Enter Play Mode the default
for new projects in 6.6 and the only mode in 6.8, where statics are not reset
between play sessions. One `-runTests`
run enters play mode once, so it cannot show a static that a session leaves
behind. `NexusVerify.PlayModeTwice` (`Nexus/Assets/Editor/PlayModeTwice.cs`) runs
the suite twice in one editor; the second run starts with what the first left
(issue #194). It refuses to run (exit 2) while domain reload is on, so set Enter
Play Mode Options to not reload the domain first, and restore the setting after:

```bash
sed -i 's/^  m_EnterPlayModeOptions: 0$/  m_EnterPlayModeOptions: 1/' Nexus/ProjectSettings/EditorSettings.asset
"/c/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe" -batchmode   -projectPath "Nexus"   -executeMethod NexusVerify.PlayModeTwice.Run   -twiceResults "artifacts/playmode-twice"   -logFile "Nexus/artifacts/playmode-twice.log"
git checkout -- Nexus/ProjectSettings/EditorSettings.asset
```

Without `-quit`: the method exits when the second run's job has ended, with 0 when
neither run failed and 1 when one did. Each run writes NUnit XML
(`artifacts/playmode-twice-run1.xml`, `-run2.xml`, relative to the project); the log
shows "Entering Playmode with Reload Domain disabled." once per run.

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
- a public type that exists only in the Editor and in players whose Managed Code Variant is Checked or Debug
  (`Nexus/Assets/Scripts/Verify/DevOnlyProbe.cs`) is named in the generated binder only under its own
  `#if` (issue #85; the release Mono build checks the same),
- nothing relies on runtime JIT: `CommandRegistry.cs`'s two generic dispatchers and the
  `DynamicMethod` emitter in `CompiledAccessorEmitter.cs`, which `NexusDI` uses, take reflection
  under `ENABLE_IL2CPP` / `UNITY_AOT`; its signal setter calls `Expression.Compile` on every
  backend, which IL2CPP's class library runs in its expression interpreter (the `unityaot`
  `System.Core` ships the interpreter and not the code-emitting compiler), and falls back to
  reflection with a warning logged in every build (#200).

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
only in the development players (their builds define `NEXUS_DEVICE_PROBE`): on the phone it logs `[Probe]` lines (`adb logcat -s Unity`) with the first resolve of the
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
which adds the core's `ToolkitPanelInput`, and `inputforui`). Each launch sends `KEYCODE_BACK` three times in each of 10 rounds (sheet, modal, root), drags over
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
is already proven valid, compiling C# that boots end to end, by the harness
(CodeGenSuite CG2, `tools/nexus-benchmark`); Mode B builds it into a real Unity
6000.6 IL2CPP player. The last run, 2026-11-02 (issues #242, #264), is in `Nexus/artifacts/modeb-20261102/`:
the IL2CPP player built, only `Assembly-CSharp` of its managed assemblies holds `Nexus.Generated.NexusGeneratedBinder`,
and its `global-metadata.dat` names it. That run built the IL2CPP player only (step 3 below); the tests of steps 1
and 2 were not run in Mode B.

`Assets/csc.rsp` reaches every assembly that has no `csc.rsp` of its own, package assemblies included (in this project
every assembly but `com.nexus.core.editor`, which has `Editor/csc.rsp`), not only the predefined ones. The generator
writes a binder only into a game's runtime assembly; a Nexus package, editor or test assembly gets none (#264).

One-file rule: only one binder may exist per assembly. Mode B removes the editor
file and lets the compiler generate the binder. While a `csc.rsp` that governs the binder's assembly names the
generator, **Generate AOT Binder** and the pre-build step of every player build write only `link.xml` (#242); an editor
binder left beside the generator stops both, with a message that names CS0101.

```bash
# 1) Switch the source generator on. Create Nexus/Assets/csc.rsp containing:
#
#      -analyzer:Packages/com.nexus.core/SourceGenerators~/Nexus.SourceGenerator.dll
#
# 2) Remove the editor-time binder so the generated class does not collide
#    (the file is git-ignored; Unity deletes its tracked .meta on the next import):
rm Nexus/Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs

# 3) Build the IL2CPP player (step 4 above); the compile now includes the source-generated binder:
"C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe" -batchmode -nographics -quit \
  -projectPath Nexus -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneIL2CPP \
  -logFile Nexus/artifacts/il2cpp-build.log

# 4) Revert (back to the editor-time binder):
rm Nexus/Assets/csc.rsp Nexus/Assets/csc.rsp.meta
git checkout -- Nexus/Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs.meta
#    then run Nexus > Generate AOT Binder (or the GenerateBinder command in CLAUDE.md).
```

Unity does not treat an analyzer named in a `csc.rsp` as a compile input: after the generator DLL is rebuilt, an
assembly keeps the binder the old DLL wrote until something else recompiles it, such as a change to its sources, its
references or the `csc.rsp` (2026-11-02 run, `README.md` there).

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
