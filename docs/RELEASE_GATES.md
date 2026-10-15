# Release gates

A package version is tagged only when every gate below is green on the commit being tagged.
Each gate names its command and its latest measured result; a gate without a fresh result on
the release commit is red. Numbers here are replaced, never accumulated: the archive under
`docs/archive/` keeps older snapshots.

Latest full run: **2026-10-15**, branch `fix/gigawatt-findings`; gates 3–5 after `e3afac7` with the typed factory and prefab injection changes, gates 8–9 after `35149a3`,
Unity **6000.5.6f1**, Windows 11. Result files: `Nexus/artifacts/port-20261015/` (local, ignored).

| # | Gate | How to run | Latest result |
|---|---|---|---|
| 1 | Host harness green | `dotnet run --project tools/nexus-benchmark` (also the pre-commit hook) | 294 checks PASS, exit 0 |
| 2 | Harness coverage known | `dotnet run --project tools/nexus-benchmark -- --coverage` | 119 of 124 runtime files compiled; out of scope: `InjectScanner`, `NexusProfiler`, `IAssetLoadService`, `ResourcesAssetLoadService`, `GcAllocationProbe` |
| 3 | Unity EditMode green | `Unity.exe -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode` | 378 passed, 0 failed, 2 skipped (need a graphics device) |
| 4 | Unity PlayMode green | `Unity.exe -batchmode -projectPath Nexus -runTests -testPlatform PlayMode` | 163 passed, 0 failed, 2 skipped (assert the `NEXUS_DEBUG`-off configuration) |
| 5 | Generated binder current | `Unity.exe -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder` | "no file changes"; `link.xml` preserves `Assembly-CSharp` and `com.nexus.core` |
| 6 | Documentation consistent | `python tools/check-docs.py` | 0 broken links; every number and path in `README.md`, the package `README.md` and this file verified against the code on 2026-10-15 |
| 7 | Single changelog | `Nexus/Packages/com.nexus.core/CHANGELOG.md` is the only changelog; `package.json` is the released version | Satisfied on 2026-10-15 (repository-root changelog folded in and removed) |
| 8 | Clean UPM consumer | `./tools/nexus-consumer/run.ps1 -RunName <name> -BuildPlayer` (packs the tarball, creates a fresh project, imports the sample, runs its tests, builds and runs a Mono player) | 2026-10-15 after `35149a3`, run `consumer-20261015b`: 7 stages exit 0 (pack, import, prepare, EditMode 2/2, PlayMode 1/1, Mono build, player `passed: true`). `-ValidatePerformance` and `-BuildAndroid` not run in this pass |
| 9 | Player builds | `Unity.exe -batchmode -nographics -projectPath Nexus -executeMethod NexusVerify.NexusVerifyBuild.BuildStandaloneMono -quit` and `...BuildAndroidIL2CPP` (`tools/unity-verify`) | 2026-10-15 after `35149a3`: Windows Mono player built (47 s, 5 warnings); Android ARM64 IL2CPP APK built (257 s, 6 warnings). Build evidence only; no physical Android/iOS execution has ever been recorded |
| 10 | Competitor comparison current | `./tools/nexus-comparison/run.ps1` and `run-unity.ps1 -ArtifactFolder comparison-20261015 [-Android]` | 2026-10-15 run `after-mono-android-002`: VContainer 1.19.0, Zenject 9.2.0, Reflex 14.4.0 pinned by commit; host and Unity 6000.5.6f1 Mono player, 12 measurements each; tables in the package `docs/PERFORMANCE_COMPARISON.md`. Nexus leads all three Mono scenarios after the DI optimisation (transient 515 ns vs Reflex 580; startup 5048 vs 5367). Android IL2CPP on a Xiaomi 2311DRK48G (Android 16): cached 32 ns (4.5× ahead), startup 29.3 µs (Reflex 24.0, VContainer 63.9), transient 6.0 µs (VContainer 3.7, Reflex 4.4) |
| 11 | Consumers re-pinned | Every game that consumes Nexus points at the release commit; no embedded package copies | Open: Gigawatt still carries an embedded copy whose corrections were ported here on 2026-10-15 (`027dab2`, `dd6ffa7`, `4b007db`); it re-pins once this branch is pushed |

## Open work before the next tagged version

- Re-run gates 1–9 on the release commit itself (this file records runs on the commits named per gate); run gate 8 with `-ValidatePerformance -BuildAndroid`.
- Windows IL2CPP comparison: Visual Studio's C++ toolchain is not installed on the
  measuring machine (no `vswhere`), so only the Android IL2CPP path exists; a comparison
  with generated bindings enabled for every candidate is also open.
  No "fastest" claim is made; see the package `docs/PERFORMANCE_COMPARISON.md`.
- IL2CPP transient construction without generated bindings is 1.6× slower than VContainer on
  the device (reflection `ConstructorInfo.Invoke` path); optimise that path and add a
  comparison configuration with generated bindings enabled for every candidate.
- Physical-device acceptance (Android IL2CPP soak, storage interruption, background/resume).
- Ad and store services now carry the contract a shipped game needed (structured outcomes,
  readiness and store-change events, localized prices, privacy options; see the package
  `docs/ADAPTERS.md`). Open: the consent flow itself (Google UMP) still has to be written inside
  the game's adapter, and no real AdMob / Unity IAP adapter ships with the package.

## Policy

- A test exists to lock a behaviour that was wrong or can regress. Tests that restate the
  implementation, assert constants, or duplicate another test are removed, not added.
- Evidence is a result file or a log from the command above, dated and tied to a commit.
  "Verified" without that is not used in any document.
- Performance claims name the workload, platform, Unity version, competitor versions and the
  reproduction tool. Host benchmarks never stand in for device measurements.
