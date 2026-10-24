# Release gates

A package version is tagged only when every gate below is green on the commit being tagged.
Each gate names its command and its latest measured result; a gate without a fresh result on
the release commit is red. Numbers here are replaced, never accumulated: the archive under
`docs/archive/` keeps older snapshots.

Latest full run: **2026-10-15**, every gate on commit `8f19c20` = tag **v0.5.0** (`main`), Unity **6000.5.6f1**, Windows 11.
Result files: `Nexus/artifacts/release-050/` (harness, coverage, binder, EditMode, PlayMode, player build logs),
`Nexus/artifacts/consumer-050/`, `Nexus/artifacts/comparison-20261015/rc-050/`, `Nexus/artifacts/comparison-20261015-generated/rc-050-generated/`,
`Nexus/artifacts/device-050/` (local, ignored). The commits after the tag (adapter packages, device-acceptance tool, this file) are under Unreleased.

| # | Gate | How to run | Latest result |
|---|---|---|---|
| 1 | Host harness green | `dotnet run --project tools/nexus-benchmark` (also the pre-commit hook) | 294 checks PASS, exit 0 (`release-050/harness.log`) |
| 2 | Harness coverage known | `dotnet run --project tools/nexus-benchmark -- --coverage` | 124 of 129 runtime files compiled; out of scope: `InjectScanner`, `NexusProfiler`, `IAssetLoadService`, `ResourcesAssetLoadService`, `GcAllocationProbe` |
| 3 | Unity EditMode green | `Unity.exe -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode` | 379 passed, 0 failed, 2 skipped (need a graphics device); `release-050/editmode.xml` |
| 4 | Unity PlayMode green | `Unity.exe -batchmode -projectPath Nexus -runTests -testPlatform PlayMode` | 163 passed, 0 failed, 2 skipped (assert the `NEXUS_DEBUG`-off configuration); `release-050/playmode.xml` |
| 5 | Generated binder current | `Unity.exe -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder` | "no file changes" on the second run after the version bump (the first run rewrote the header); `link.xml` preserves `Assembly-CSharp` and `com.nexus.core` |
| 6 | Documentation consistent | `python tools/check-docs.py` | 72 files, 0 broken links; every number and path in `README.md`, the package `README.md` and this file verified against the code on 2026-10-15 |
| 7 | Single changelog | `Nexus/Packages/com.nexus.core/CHANGELOG.md` is the only changelog; `package.json` is the released version | Satisfied: `package.json` 0.5.0 = `## [0.5.0] - 2026-10-15`; the adapter packages' changes are recorded in the same file |
| 8 | Clean UPM consumer | `./tools/nexus-consumer/run.ps1 -RunName <name> -BuildPlayer -BuildAndroid -ValidatePerformance` (packs the tarball, creates a fresh project, imports the sample, runs its tests, builds and runs a Mono player, builds an Android IL2CPP player) | run `consumer-050` on `8f19c20`: 15 stages exit 0 (pack, import, prepare, EditMode 2/2, PlayMode 1/1, release performance 10/10, trace stripping 1/1, hardening 41/41, readiness 24/24, services 13/13, reentrancy 8/8, lifecycle 6/6, Mono build, player `passed: true`, Android IL2CPP build) |
| 9 | Player builds | `Unity.exe -batchmode -nographics -projectPath Nexus -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneMono -quit` and `...BuildAndroidIL2CPP` (`tools/unity-verify`) | on `8f19c20`: Windows Mono player built (18.5 s, 5 warnings); Android ARM64 IL2CPP APK built (127.9 s, 6 warnings); `release-050/*-build.log` |
| 10 | Competitor comparison current | `./tools/nexus-comparison/run.ps1 [-Generated]` and `run-unity.ps1 -ArtifactFolder comparison-20261015[-generated] -Android [-Generated]` | on `8f19c20`, VContainer 1.19.0, Zenject 9.2.0, Reflex 14.4.0 pinned by commit; host, Unity 6000.5.6f1 Mono player and Android IL2CPP on a Xiaomi 2311DRK48G (Android 16), 12 measurements each, two configurations; tables in the package `docs/PERFORMANCE_COMPARISON.md` (runs `after-reset-android-006` / `generated-android-002` on the working tree before the tag; `rc-050` / `rc-050-generated` on the tag repeat them within drift). Reflection `rc-050` device: cached 28 ns (VContainer 161, Reflex 218), transient 4.9 µs (VContainer 3.4, Reflex 3.1), startup 12.9 µs (Reflex 14.5, VContainer 34.5). Generated `rc-050-generated` device: cached 28 ns, transient 2.1 µs (VContainer 1.0, Reflex 4.4), startup 27.6 µs (Reflex 28.9, VContainer 57.3). Device startup drifts up to 2× between launches for unchanged libraries; compare within a run |
| 11 | Consumers re-pinned | Every game that consumes Nexus points at the release commit by git URL; no embedded copies, no `file:` paths, no submodules | Open. Gigawatt (core, `com.nexus.ads.admob`, `com.nexus.iap.unity`), Wickling and ChipCrisis (those three and `com.nexus.firebase`) pinned at `c7a31eb` on 2026-10-24: Gigawatt commit `5dee30a` (`gw_ci.py unity-compile` 4 / 0, `nexus_regressions.py` 7 / 0), Wickling `6f57943` (`wk_ci.py unity-compile` 9 / 0), ChipCrisis `6a5f364` (`run-tests.ps1` EditMode 229 / 228 / 0 / 1, PlayMode 2 / 2 / 0 / 0); binders regenerated unchanged. A consumer that breaks on a pin opens an issue in this repository. Pixel-Flow-Clone and RingFlow: manifests pinned to `8f19c20` on 2026-10-15 but their game code does not compile against 0.5.0 (`WindowManager` removed in 0.5.0; custom `EconomyService`/`ProgressionService` behind the current interfaces), evidence `Nexus/artifacts/consumers-050/`; their `GameContainer` submodules are still present. IdleProject: still `file:../GameContainer` plus a submodule. PuzzleGame: embedded copy at 0.2.0 with local edits, Unity 6000.4.10f1 (not installed here) |
| 12 | Device acceptance | `./tools/device-acceptance/run.ps1 -RunName <name>` on the Android IL2CPP player of gate 9 (cold start, 5 min soak with memory samples, background/resume, kill/restart, logcat scan) | run `device-050` on the `8f19c20` player: passed (cold start, 5 min soak alive with PSS 214.8 → 255.6 MB (+19%, limit 50%), background and resume on the same process, restart after force-stop, 0 fatal exceptions / ANR / Unity exceptions in the app's own logcat lines); `device-050/device-acceptance.json`, `logcat.log`. The framework player is the URP template scene plus NexusStarter; a game's player goes through the same tool |

## Open work before the next tagged version

- Consumers (gate 11): Pixel-Flow-Clone and RingFlow game-code migration to 0.5.0 (`MIGRATION.md`), then submodule removal; IdleProject pin; PuzzleGame editor upgrade and embedded-copy removal.
- Windows IL2CPP comparison: Visual Studio's C++ toolchain is not installed on the
  measuring machine (no `vswhere`), so only the Android IL2CPP path exists.
  No "fastest" claim is made; see the package `docs/PERFORMANCE_COMPARISON.md`.
- With generated bindings VContainer constructs the transient graph 2.1× faster than Nexus on
  the device (1.0 vs 2.1 µs): its generated injector is one call per type, while Nexus's
  generated factory resolves every constructor parameter through the container's full resolve
  path. Next lever: a parameter-resolve fast path for generated factories, accepted only on a
  device run with repeated launches (device numbers drift up to 2× between launches).
- Adapter packages `com.nexus.ads.admob` and `com.nexus.iap.unity` (Unreleased): compiled against the real SDKs in a disposable project and unit-tested before initialization, plus a deferred payment and the consent debug settings (`Nexus/artifacts/adapters-20261020/editmode.xml`: 10 passed, 0 failed, 0 skipped). Gigawatt runs both in its signed Android IL2CPP release build (device acceptance passed; a Google test rewarded ad paid out on the phone) and saw the consent form with the EEA debug geography on its phone (Google's test message, consent obtained, then the SDK started); a price, purchase and restore against Google Play are still open (the product is not yet defined in Play Console).
- Device acceptance covers the framework's own player (boot and idle); a game's player goes through the same tool (`-Apk`).

## Policy

- A test exists to lock a behaviour that was wrong or can regress. Tests that restate the
  implementation, assert constants, or duplicate another test are removed, not added.
- Evidence is a result file or a log from the command above, dated and tied to a commit.
  "Verified" without that is not used in any document.
- Performance claims name the workload, platform, Unity version, competitor versions and the
  reproduction tool. Host benchmarks never stand in for device measurements.
