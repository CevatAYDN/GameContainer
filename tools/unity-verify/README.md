# Unity 6000.5.6 Compile Verification (Mono + IL2CPP)

The `.NET` harness in `tools/nexus-benchmark` compiles the real `com.nexus.core`
sources and proves behavior, but it cannot prove the **Unity pipeline** compiles:
editor assemblies, the demo scene, URP shader/graphics settings, and — most
critically — the **IL2CPP/AOT** path (code stripping, `link.xml` preservation,
generic dispatch survival without a JIT).

This folder is the missing half: a repeatable, headless command sequence that
compiles the demo project under Unity **6000.5.6f1** with both scripting
backends.

## Prerequisites

- Unity **6000.5.6f1** (matches `Nexus/ProjectSettings/ProjectVersion.txt`),
  installed via Unity Hub, with the **Windows Build Support** module.
  - Optional: **Android Build Support** (SDK + NDK) if you want the Android
    IL2CPP step.
- The editor must be activated (license) on the machine.

## Quick start

```bash
# from the repo root
UNITY_PATH="C:/Program Files/Unity/Hub/Editor/6000.5.6f1/Editor/Unity.exe" \
  bash tools/unity-verify/verify-unity-build.sh
```

Logs and test result XMLs land in `tools/unity-verify/artifacts/`.

## The four verification steps (manual command list)

### 1. EditMode tests — editor + runtime compile under Mono

```bash
"<Unity 6000.5.6f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -runTests -testPlatform EditMode \
  -testResults "tools/unity-verify/artifacts/editmode-results.xml" \
  -logFile "tools/unity-verify/artifacts/editmode.log" -quit
```

Compiles every editor + runtime assembly and runs the NUnit EditMode suite.
Any `result="Failed"` in the XML is a failure.

### 2. PlayMode tests — runtime boots and dispatches in a player context

```bash
"<Unity 6000.5.6f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -runTests -testPlatform PlayMode \
  -testResults "tools/unity-verify/artifacts/playmode-results.xml" \
  -logFile "tools/unity-verify/artifacts/playmode.log" -quit
```

Boots `NexusStarter` and runs the PlayMode suite (context lifecycle, signal
dispatch, pooling, storage) against the real Unity runtime.

### 3. Mono player build — full player compile (Mono backend)

```bash
"<Unity 6000.5.6f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.Build.BuildStandaloneMono \
  -logFile "tools/unity-verify/artifacts/mono-build.log" -quit
```

Produces `Nexus/builds/standalone-mono/`. A success proves the whole game
(scripts + scene + URP) compiles into a runnable Mono player.

### 4. IL2CPP player build — code-stripping + AOT verification

```bash
"<Unity 6000.5.6f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.Build.BuildStandaloneIL2CPP \
  -logFile "tools/unity-verify/artifacts/il2cpp-build.log" -quit
```

Produces `Nexus/builds/standalone-il2cpp/`. This is the strongest signal:
IL2CPP compiles the C# to C++ and strips unused code, so it validates that
- `Runtime/link.xml` preserves the generic dispatch entry points,
- the `[Preserve]`-annotated runtime surface survives stripping,
- nothing relies on runtime JIT (`Expression.Compile` is bypassed under
  `ENABLE_IL2CPP` in `NexusDI.cs`).

### Optional: Android IL2CPP (needs SDK/NDK)

```bash
"<Unity 6000.5.6f1>/Editor/Unity.exe" -batchmode -nographics \
  -projectPath "Nexus" \
  -executeMethod NexusVerify.Build.BuildAndroidIL2CPP \
  -logFile "tools/unity-verify/artifacts/android-il2cpp-build.log" -quit
```

The most realistic mobile pipeline (Android + IL2CPP + stripping).

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
