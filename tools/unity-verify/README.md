# Unity 6000.6.8 Compile Verification (Mono + IL2CPP)

The `.NET` harness in `tools/nexus-benchmark` compiles the real `com.nexus.core`
sources and proves behavior, but it cannot prove the **Unity pipeline** compiles:
editor assemblies, the demo scene, URP shader/graphics settings, and — most
critically — the **IL2CPP/AOT** path (code stripping, `link.xml` preservation,
generic dispatch survival without a JIT).

This folder is the missing half: a repeatable, headless command sequence that
compiles the demo project under Unity **6000.6.8f1** with both scripting
backends.

## Prerequisites

- Unity **6000.6.8f1** (matches `Nexus/ProjectSettings/ProjectVersion.txt`),
  installed via Unity Hub, with the **Windows Build Support** module.
  - Optional: **Android Build Support** (SDK + NDK) if you want the Android
    IL2CPP step.
- The editor must be activated (license) on the machine.
- Python 3.11+ (standard library only) and Git Bash on Windows
  (`C:\Program Files\Git\bin\bash.exe`). No packages need installing.
- Run only while no other Unity process owns `Nexus/`; do not edit its source
  during verification. `UNITY_PATH` must name the installed pinned editor above
  (a fake editor override is used only by the runner's regression tests).

## Quick start

```bash
# from the repo root
UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe" \
  bash tools/unity-verify/verify-unity-build.sh
```

Each invocation creates `tools/unity-verify/artifacts/run-<UTC>-<unique>/`.
An explicit `UNITY_VERIFY_EVIDENCE_DIR` may name a new folder whose parent
already exists; the runner refuses to reuse it, including after a failed run.
Logs, XML, `source.commit` and validated build JSON records remain there on
failure. No existing evidence, build folder or user data is deleted.

The runner keeps four stages and stops on any nonzero editor exit or incomplete
evidence. Tests omit `-quit` because the test runner owns termination. Python
parses the `test-run` root and checks root and suite counts against actual test cases:
missing/malformed XML, failed or inconclusive results, zero passed tests,
skipped-only runs and inconsistent counts fail. Passed runs with legitimate
skips are allowed. The observed Unity Test Framework 1.8 aggregate
`result="Skipped:Ignored"` is also accepted, but only with both passed and skipped
tests, zero failures/inconclusives, and consistent counts and nested states
(`Passed`, or `Skipped` with `label="Ignored"`). A skipped-only child suite is
allowed inside such a mixed run. Other aggregate results fail. The actual root
result and passed/failed/inconclusive/skipped counts are printed. Nested
result/label combinations are checked under both accepted roots: a `Passed`
root also permits ordinary unlabeled or `Explicit` skips, but unknown labels
cannot bypass validation by changing only the aggregate result.

For each Windows build it requires a unique success line from the current log
matching `NexusVerifyBuild`'s target and backend (`Mono2x` or `IL2CPP`), nonempty
executable, `UnityPlayer.dll`, serialized player data, backend output (Mono
managed assembly or IL2CPP `GameAssembly.dll` and metadata), and a matching
`.commit` including its dirty annotation. Serialized `globalgamemanagers` and
the commit sidecar must have been written after that stage started; copied
engine templates may retain old timestamps. Build JSON records file hashes,
sizes and timestamps. An existing directory or exit 0 alone is not proof.
These are build checks, not proof that the player was launched or accepted on
a device. The runner starts each stage on `Win64` to avoid stale Android defines.

Focused tool regressions (fake editors only, no Unity/device/host harness):

```powershell
& "C:\Program Files\Git\bin\bash.exe" -n tools/unity-verify/verify-unity-build.sh
python -B -m unittest discover -s tools/unity-verify -p test_verify_unity_build.py -v
```

## The four verification steps (manual command list)

Prefer the runner above for validated results. For manual investigation, use
absolute paths: Unity resolves relative `-testResults` against the project,
but relative `-logFile` against the working directory. Create fresh evidence:

```bash
ROOT="$(pwd)"
mkdir -p "$ROOT/tools/unity-verify/artifacts"
EVIDENCE="$(mktemp -d "$ROOT/tools/unity-verify/artifacts/manual-XXXXXX")"
```

### 1. EditMode tests — editor + runtime compile under Mono

```bash
"<Unity 6000.6.8f1>/Editor/Unity.exe" -batchmode -nographics \
  -buildTarget Win64 -projectPath "$ROOT/Nexus" \
  -runTests -testPlatform EditMode \
  -testResults "$EVIDENCE/editmode-results.xml" \
  -logFile "$EVIDENCE/editmode.log"
python tools/unity-verify/check_results.py tests "$EVIDENCE/editmode-results.xml"
```

Compiles every editor + runtime assembly and runs the NUnit EditMode suite.
The structured checker rejects incomplete and unsuccessful results.

### 2. PlayMode tests — runtime boots and dispatches in a player context

```bash
"<Unity 6000.6.8f1>/Editor/Unity.exe" -batchmode -nographics \
  -buildTarget Win64 -projectPath "$ROOT/Nexus" \
  -runTests -testPlatform PlayMode \
  -testResults "$EVIDENCE/playmode-results.xml" \
  -logFile "$EVIDENCE/playmode.log"
python tools/unity-verify/check_results.py tests "$EVIDENCE/playmode-results.xml"
```

Runs the PlayMode suite (context lifecycle, signal dispatch, pooling, storage)
against the real Unity runtime. `NexusStarterSceneTests` loads `NexusStarter` and clicks its
UI Toolkit button; the player builds below boot it.

### 2b. PlayMode twice with domain reload off

Unity's "Path to CoreCLR" guide (2026-03-26) makes Fast Enter Play Mode the default
for new projects in 6.6 and the only mode in 6.8, where statics are not reset
between play sessions. One `-runTests`
run enters play mode once, so it cannot show a static that a session leaves
behind. `NexusVerify.PlayModeTwice` (`Nexus/Assets/Editor/PlayModeTwice.cs`) runs
the suite twice in one editor; the second run starts with what the first left
(issue #194). It refuses to run (exit 2) while domain reload is on, so set Enter
Play Mode Options to not reload the domain first. Record the original value and
existing diff; afterwards restore only the value changed for this run, preserving
all pre-existing edits:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe" -batchmode \
  -projectPath "$ROOT/Nexus" -executeMethod NexusVerify.PlayModeTwice.Run \
  -twiceResults "$EVIDENCE/playmode-twice" -logFile "$EVIDENCE/playmode-twice.log"
python tools/unity-verify/check_results.py tests "$EVIDENCE/playmode-twice-run1.xml"
python tools/unity-verify/check_results.py tests "$EVIDENCE/playmode-twice-run2.xml"
```

Without `-quit`: the method exits when the second run's job has ended, with 0 when
neither run failed and 1 when one did. Each run writes NUnit XML
(`playmode-twice-run1.xml`, `-run2.xml` in the chosen evidence folder); the log
shows "Entering Playmode with Reload Domain disabled." once per run.

### 3. Mono player build — full player compile (Mono backend)

```bash
"<Unity 6000.6.8f1>/Editor/Unity.exe" -batchmode -nographics \
  -buildTarget Win64 -projectPath "$ROOT/Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneMono \
  -logFile "$EVIDENCE/standalone-mono-build.log" -quit
```

Produces `Nexus/builds/standalone-mono/` (each Windows player builds into its own folder, issue #102).
Every build also writes `Nexus/builds/<name>.commit`: the commit it was built from, marked
"(Packages or Assets modified)" when those folders differed from it (issue #105).
A success proves the whole game
(scripts + scene + URP) compiles into a runnable Mono player.

### 4. IL2CPP player build — code-stripping + AOT verification

```bash
"<Unity 6000.6.8f1>/Editor/Unity.exe" -batchmode -nographics \
  -buildTarget Win64 -projectPath "$ROOT/Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneIL2CPP \
  -logFile "$EVIDENCE/standalone-il2cpp-build.log" -quit
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
"<Unity 6000.6.8f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "$ROOT/Nexus" \
  -buildTarget Android -executeMethod NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPP \
  -logFile "$EVIDENCE/android-il2cpp-build.log" -quit
```

The most realistic mobile pipeline (Android + IL2CPP + stripping). Start every Android build with `-buildTarget Android`: editor code a package compiles only under `#if UNITY_ANDROID` runs only in an editor that starts on Android. A build started on Windows switches the target inside the build call, without those assemblies, and its player lacks what they add, such as the notification permission and receiver of `com.unity.mobile.notifications` (#160, `Nexus/artifacts/notifications-20261102/`).

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
parameter types: an overload with the same shape can hide a missing implementation.
The packages' assemblies in that folder show what their `link.xml` files kept, and the
probe's `framework services resolved 4/4 injected 4/4` line shows the services still construct and
inject. Unity has no call that unsets a stripping level, so the build writes the previous level back
explicitly. Record the settings and pre-existing diff before the run; afterwards
restore only verification-owned changes to `ProjectSettings.asset`. Never
restore the whole file from Git over unrelated work.

`NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPPDevelopment` builds the same player as a development
build (`Nexus/builds/android-il2cpp-dev.apk`) with `Assets/Scripts/Verify/DeviceProbe.cs`, which exists
only in the development players (their builds define `NEXUS_DEVICE_PROBE`): on the phone it logs `[Probe]` lines (`adb logcat -s Unity`) with the first resolve of the
framework services Gigawatt binds (issue #110), the managed allocations of each haptic play (issue #84) and, after each HOME and return, the time away by the wall clock,
`Stopwatch`, `SystemClock.elapsedRealtime` and `OfflineTimeCalculator` (issue #77). Both Android players
have the same application id, so installing one replaces the other: before a probe run, compare
`adb shell dumpsys package <application id> | grep lastUpdateTime` with the APK's time and look for a
`[Probe]` line after the launch.

The same development player holds `Assets/Scripts/Verify/ServicesProbe.cs`, the phone check of the share sheet
(issue #134), local notifications (#160) and the debug menu (#270), which runs only when the player starts with the
intent extra `nexusProbe` (`services`, `services-cold`, `services-ask`, `services-menu`), so a plain launch is
unchanged. `pwsh tools/unity-verify/services-probe.ps1 -AdbSerial <serial>` installs the development APK and makes
seven launches, each of which must pass:

- `no-permission` and `permission` (`POST_NOTIFICATIONS` revoked, then granted): the share sheet for a text and then a
  file (each must cover the game; Back closes it), the file sheet's package holding a read grant on the provider URI
  while it is open (`dumpsys activity permissions`) and no `Permission Denial: opening provider` line in logcat from
  its preview (issue #274), the shared file read by adb's shell through the `content://` URI the probe grants it, a
  notification scheduled 20 s ahead, the debug menu opened and closed with a held Backquote key
  (adb's plain key event releases at once and was missed at 1 of 12 presses, likely press and release in one frame),
  then HOME and a wait of up to 90 s, since Android posts an inexact alarm late. Without the permission nothing may be
  posted; with it a tap on the notification must bring the game back with `TakeOpenedWith` returning `probe-data` and
  remove it.
- `cold`: the notification is scheduled, the process is killed in the background (`run-as <id> kill`; force-stop would
  cancel the alarm), and the tap must start a new process whose `TakeOpenedWith` returns `probe-data`.
- `ask-deny`, `ask-deny-again`, `ask-after-two-denials`, `ask-allow`: `RequestPermissionAsync` after `pm clear`, with
  Android's dialog answered by its button ids; after two refusals the request must answer `NeedsPermission` without one.

`-ThreeFingers` makes only the `three-fingers` launch, attended: someone at the phone opens the debug menu with three
fingers, lifts them, and closes it with three fingers; the menu's lines carry the touches seen. The permission and its
flags are restored afterwards. The evidence goes to `Nexus/artifacts/<RunName>/` (`services-probe.json`, and
`probe-<run>.txt`, `logcat-<run>.txt` and the notification record or screen dump for each launch); exit 0 passed,
1 failed, 2 could not run.

The project builds with minify off, so R8 never runs on the packages' Android libraries there. With `-nexusMinify`,
`BuildAndroidIL2CPPDevelopment` builds the development player with R8 (`Nexus/builds/android-il2cpp-dev-minify.apk`,
the setting restored afterwards): R8's `configuration.txt`, `seeds.txt` and `usage.txt` under
`Nexus/Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build/outputs/mapping/debug/` show the keep rules it took, what
they kept and what it removed, and `services-probe.ps1 -Apk Nexus/builds/android-il2cpp-dev-minify.apk` checks the
player (#134's `NexusFileProvider` through the file share).

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

### Persistence restart probe (#338)

`persistence-probe.ps1` drives `Assets/Scripts/Verify/PersistenceProbe.cs` in the
framework Android IL2CPP development player. Supply the frozen APK's SHA256,
exact `.commit` text (including `(Packages or Assets modified)` when present),
explicit authorized device serial and installed `aapt` executable:

```powershell
pwsh tools/unity-verify/persistence-probe.ps1 `
  -Apk Nexus/builds/android-il2cpp-dev.apk `
  -ExpectedApkSha256 <64-hex-sha256> -ExpectedCommit "<exact-build-commit>" `
  -AdbSerial <serial> -Aapt "<installed-Android-build-tools>/aapt.exe"
```

The mandatory identity checks freeze the APK and sidecar in a fresh
`Nexus/artifacts/<RunName>/` and verify them before installation and each phase.
The APK must be debuggable with exactly the framework package
`com.UnityTechnologies.com.unity.template.urpblank`; no game package is allowed.
Installation uses `adb install -r`, with no game clear or uninstall. The probe
must not already be running. It performs 12 launches: bool/outcome callbacks
times wallet/ack faults times arm/recover/verify, checking run/phase/PID markers,
receipts, reopened balance, pending acknowledgement and confirmed force-stop
termination. Evidence includes `persistence-probe.json` and each phase's log.
Exit 0 means all 12 restart checks were observed; 1 means failed/incomplete
phases; 2 means preflight could not run. This is process-restart, simulator,
single-wallet-JSON evidence, not power-loss, live ad SDK, multi-key transaction
or consumer-game acceptance.

Within the same 12 launches, the first bool/wallet scenario also checks #339's
file-backed long and BigDouble balances: corrupt reads must throw and disposal
must preserve the corrupt bytes, then explicit valid import must recover them.
After saving a mutation, both later restart phases require balances of 1005;
these additional economy checks do not constitute a multi-key transaction proof.

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

One-file rule: only one binder may exist per assembly. Mode B temporarily moves the editor
file and lets the compiler generate the binder. While a `csc.rsp` that governs the binder's assembly names the
generator, **Generate AOT Binder** and the pre-build step of every player build write only `link.xml` (#242); an editor
binder left beside the generator stops both, with a message that names CS0101.

Before switching, preserve the current binder and its `.meta` outside `Assets`,
and record any existing `Assets/csc.rsp` contents and diff. Add only the analyzer
line below to the response file, preserving other compiler options, then move
the editor-time binder and its `.meta` out of `Assets`:

```text
-analyzer:Packages/com.nexus.core/SourceGenerators~/Nexus.SourceGenerator.dll
```

Build the IL2CPP player (step 4 above); the compile now includes the source-generated binder:

```bash
"C:/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe" -batchmode -nographics -quit \
  -buildTarget Win64 -projectPath "$ROOT/Nexus" \
  -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneIL2CPP \
  -logFile "$EVIDENCE/modeb-il2cpp-build.log"
```

To return, remove only the analyzer line added for this run and restore the
preserved binder and `.meta`. A newly created response file and `.meta` may be
removed only when they are still solely this run's files; preserve any concurrent
edits. Then run Nexus > Generate AOT Binder (or the command in `CLAUDE.md`).
Inspect the scoped diff rather than using wholesale checkout or deletion.

Unity does not treat an analyzer named in a `csc.rsp` as a compile input: after the generator DLL is rebuilt, an
assembly keeps the binder the old DLL wrote until something else recompiles it, such as a change to its sources, its
references or the `csc.rsp` (2026-11-02 run, `README.md` there).

## How to interpret a failure

Each step is a separate Unity batch process that exits non-zero on failure and
prints the tail of its log. Common failure classes and where to look:

| Symptom | Log line | Likely cause |
|---|---|---|
| `error CS####` | `standalone-mono-build.log` / `standalone-il2cpp-build.log` | C# compile error — fix and re-run |
| `Build FAILED ... result=Failed` | build log | Asset/scripting issue at link time |
| `Internal build system has thrown an exception` | build log | Missing build-support module |
| `result="Failed"` in test XML | `editmode-results.xml` | Test assertion failure — inspect `<test-case>` entries |

The `.NET` harness (`tools/nexus-benchmark`) and this Unity pipeline are
complementary: run both before merging runtime changes.
