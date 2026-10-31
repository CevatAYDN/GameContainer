# Nexus repository guide

Nexus is an open-source Unity 6 MVCS framework (`com.nexus.core`) meant to be used as the
foundation of several games and by developers of every level. Everything in this repository
must be current, consistent and verifiable. Read this file before changing code or documents.

## Map

| Path | Role |
|---|---|
| `Nexus/Packages/com.nexus.core/` | The product. `Runtime/` (player code), `Editor/` (tools, codegen, validation), `Tests/Editor` + `Tests/Runtime` (Unity NUnit), `Samples~/` (3 importable samples), `SourceGenerator~/` (Roslyn binder generator), `SourceGenerators~/` (its built analyzer DLL), `docs/` (user documentation), `CHANGELOG.md` (the changelog of the core and its adapter packages) |
| `Nexus/` | Unity 6000.5.6f1 development project hosting the package. `Assets/Scripts/Game/Samples` holds the wizard's canonical generated files; `Assets/Scripts/Nexus/` holds the git-ignored generated binder |
| `tools/nexus-benchmark/` | .NET host harness; compiles the runtime against stubs (`UnityStubs.cs`, `GeneratorStubs.cs`) and runs every proof suite. Runs as the git pre-commit hook |
| `tools/nexus-compile-check/` | Compiles every runtime file, the files the harness leaves out included, against the installed editor's engine modules |
| `tools/nexus-comparison/` | Pinned VContainer/Zenject/Reflex DI comparison (host, Unity Mono player and Android IL2CPP device) |
| `tools/nexus-consumer/` | Clean UPM consumer gate: packs the tarball, fresh project, sample import, tests, player |
| `tools/unity-verify/` | Player builds of the development project (`NexusVerify.NexusVerifyBuild.*`) |
| `tools/device-acceptance/` | Runs an Android player on the connected phone: soak, background/resume, kill/restart, logcat scan (gate 12) |
| `Nexus/Packages/com.nexus.ads/` | The ad package family's core (issue #8): `Runtime/Core` (engine-free, `noEngineReferences`; compiled and proven in the host harness), `Runtime/Unity` (settings asset, bootstrap that can wait for `StartAds()`, simulator overlay, rewarded button), `docs/POLICY_COMPLIANCE.md` (policy register, EN + TR), `Editor/PlatformCatalog.json` (setup wizard data), `Editor/` (Project Settings page, setup wizard with Prepare Android (`AndroidSetup`, `PrepareCli`), Placement Tester, `AdsValidator` with `RunCli` and the pre-build hook), `CHANGELOG.md` (the family's changelog: core, providers, bridge). `com.nexus.ads.levelplay` is the first provider (#13), `Nexus.Ads.AdMob` in `com.nexus.ads.admob` the second (#14); a MAX provider is not planned while LevelPlay is the only mediation (#86). `MIGRATION.md` moves a game from the legacy `AdMobAdapter` to the family |
| `Nexus/Packages/com.nexus.ads.levelplay/` | `LevelPlayProvider`, the family's `IAdProvider` over `com.unity.services.levelplay` 9.x; compiles only with that package (`NEXUS_LEVELPLAY`); compile proof in a disposable project under `Nexus/artifacts/levelplay-<date>/` (gate row in `docs/RELEASE_GATES.md`) |
| `Nexus/Packages/com.nexus.ads.bridge.nexus/` | The family's adapter for `com.nexus.core`'s `AdService` (`NexusAdsAdapter`, `NexusAdsBridge.Connect`, `IapAdFreeEntitlement`; `FirebaseAdsRemoteConfig` compiles only with `com.nexus.firebase`). A Nexus game keeps its `IAdService` code and connects once |
| `Nexus/Packages/com.nexus.fsm/`, `com.nexus.dots/` | Split from the core in #112 with their namespaces kept: `GameStateMachine` (its Dispose uses the core's internal `SafeAsyncRunner`) and the window's State Machine tab (`FSMPlugin`, strings and styles from the core's editor); both reach the core through `InternalsVisibleTo`; `NativeSignalQueue<T>`/`DOTSSignalBridge<T>`, compiled only with `com.unity.collections`. A game that names their types adds the package at the core's commit: Formatron has it, RingFlow adds it at its re-pin |
| `Nexus/Packages/com.nexus.ads.admob/`, `com.nexus.iap.unity/` | `com.nexus.ads.admob` holds `Runtime/Provider` (`Nexus.Ads.AdMob`: the family's AdMob provider and `UmpConsentFlow`, Google's consent form, which the bootstrap also runs before LevelPlay; compiles only with `com.nexus.ads` and Google Mobile Ads 11.3.0+; compile proof in `Nexus/artifacts/admob-<date>/`) and `Runtime/Firebase` (`Nexus.Ads.AdMob.Firebase`: `UmpAnalyticsConsent`, the analytics consent `com.nexus.firebase` reads, compiled only with that package too; #87); its legacy `AdMobAdapter` behind `AdService` was removed in #14. `com.nexus.iap.unity` is the optional Unity IAP 5 adapter behind `IapService`; it compiles only when its SDK package is installed (`versionDefines`) |
| `docs/RELEASE_GATES.md` | The gates and the latest measured numbers |
| `docs/research/` | Dated, sourced research (competitors) |
| `docs/archive/` | Past audits, reviews and roadmaps; evidence only, never updated |

## Commands

```powershell
dotnet run --project tools/nexus-benchmark                       # host harness, ~10 s
dotnet run --project tools/nexus-benchmark -- --coverage         # which runtime files it compiles
dotnet build tools/nexus-compile-check                           # every runtime file against the real engine modules
python tools/check-docs.py                                       # markdown link check
python tools/check-versions.py [--newest]                        # dependency versions; --newest reads each source (gate 13)
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode -testResults artifacts\editmode.xml -logFile Nexus\artifacts\editmode.log
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -projectPath Nexus -runTests -testPlatform PlayMode -testResults artifacts\playmode.xml -logFile Nexus\artifacts\playmode.log
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder   # after removing or renaming an injectable type add -ignorecompilererrors (TROUBLESHOOTING "Stale Generated Binder"); never empty the binder by hand
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath Nexus -executeMethod Nexus.Ads.Editor.AdsValidator.RunCli -adsTab Release -adsPlatform Android -adsReport artifacts/ads-validation.json   # ads settings check; exit 0 pass, 1 fail, 2 could not run; the report path is relative to the project folder
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -projectPath <consumer> -buildTarget Android -executeMethod Nexus.Ads.Editor.AndroidSetup.PrepareCli -adsTab Test   # Prepare Android for the selected mediation, without -quit; exit 0 done, 1 steps remain, 2 could not run, 3 packages added (run again)
./tools/nexus-comparison/run-unity.ps1 -RunName <name> -ArtifactFolder comparison-<date> -Android            # reflection configuration: host, Mono player, Android device
./tools/nexus-comparison/run-unity.ps1 -RunName <name> -ArtifactFolder comparison-<date>-generated -Android -Generated   # generated bindings (own artifact folder: the analyzer and the define stay in the project)
```

A relative `-testResults` path is resolved against the project folder (so `artifacts\editmode.xml` lands in
`Nexus/artifacts/`), a relative `-logFile` against the working directory. Unity runs one instance per project: never start a Unity command while another is running on
`Nexus/`, and do not edit files under `Nexus/` while a batch run is in progress.
A player build leaves the editor on that build target; `NexusVerifyBuild` switches back, but after any
other Android build run `Unity.exe -batchmode -nographics -quit -projectPath Nexus -buildTarget Win64`
before tests, or the Standalone define `NEXUS_DEBUG` silently drops out of editor compilation.

## Rules

**Code**
- Runtime code must compile in the host harness too. A new public API used by `NexusCodeGenerator.cs` needs its mirror in `tools/nexus-benchmark/GeneratorStubs.cs`.
- Keep the allocation contracts in the package README "Performance scope" table; a change on a hot path needs a harness proof (GCAudit or a suite assertion).
- A DI hot-path change is accepted on the Android IL2CPP comparison run, never on host numbers alone (a change that cut host transient cost by a quarter tripled device cached resolve on 2026-10-15 and was reverted).
- Generated registrations (constructor factories, injectors, clearers) are code, not caches: `NexusDI.ClearCaches()` and `NexusRuntime.Reset()` keep them. Tests and tools that want the reflection path call `NexusDI.ClearGeneratedRegistrations()`.
- Files use CRLF line endings on disk (`core.autocrlf=true`); new `.cs` and `.md` files follow that.
- New source files under the package need their Unity `.meta` file: run any Unity command once and commit the generated `.meta` with the file.
- UI the packages add from 2026-10-31 on, runtime and editor, is UI Toolkit (`UIDocument` or `EditorWindow.rootVisualElement`, UXML/USS, `VisualElement`): no new IMGUI (`OnGUI`, `GUILayout`, `EditorGUILayout`) and no new uGUI. What exists stays as it is (the owner's decision of 2026-10-31, #137): the uGUI `UIManager`/`UICanvasSystem` (ADR-0003 decision 2), the IMGUI development panels `AdsDebugPanel`, `SimulatorOverlay` and `NexusDebugHUD`, and the IMGUI editor code (the core's inspectors under `Editor/Inspector`, the ads Project Settings page, setup wizard and Placement Tester). A fix inside them is not a rewrite.

**Dependencies**
- The framework runs on the newest stable version of everything it depends on, and every version number agrees with the others (the owner's request of 2026-10-31, #138). The Dependencies table in `docs/RELEASE_GATES.md` holds one row per dependency: the editor, each package the packages declare or compile against, the source generator's Roslyn, the host tools' .NET SDK, the competitors of gate 10, and the development project's own packages. Each row gives the declared minimum, the version a gate or a proof project ran (proven), the newest stable from the row's source, and the decision when proven and newest differ. A pre-release never counts as newest, except for a package that has only pre-releases. A built-in package and the generator's Roslyn follow the editor.
- Every gate cycle runs `python tools/check-versions.py --newest`. A newest that is not proven is proven in that cycle (its proof project, its tests, and the device run where a gate requires one), or the row says why not. A minimum is never above the proven version, and a minimum that no run used becomes the proven one. Offline, the script fails when declarations disagree (`package.json`, asmdef `versionDefines`, the table), when the development project resolves something other than its manifest line, on a git dependency not pinned to a 40-character commit, on an installed-editor path that is not the project's editor, and when the source generator references another Roslyn than the editor's compiler.
- Unity editor: every gate cycle takes the newest patch of Unity's Supported stream (the release API's `SUPPORTED` stream), also when that stream is newer than the project's. A stream move carries one migration issue per game, opened with the target that needs it. The gate-11 games run the framework's editor patch, and gate 11 records each game's editor beside its pin (the owner's decisions of 2026-10-31, #139).
- Upgrading a dependency is a change like any other: an issue, a proof folder dated in `Nexus/artifacts/`, the table row, the changelog of its family.

**Tests**
- A test locks a behaviour that was wrong or can regress, with a name that states the behaviour. No tests that restate the implementation, assert constants, duplicate another test, or exist to raise a count.
- Package EditMode tests live in `Tests/Editor` under namespace `Nexus.Editor.Tests`; runtime/PlayMode tests in `Tests/Runtime`.
- Report test results as numbers from the result file (passed / failed / skipped), never "all green" without them.

**Documentation**
- One canonical document per topic. User documentation lives in the package `docs/`; repository-level status lives in `docs/`; dated reports go to `docs/archive/` with a row in its README and are never edited afterwards.
- English is canonical; `*_TR.md` files are translations and change in the same commit as their source.
- Every number (test counts, file counts, versions, benchmark values) and every path or menu name in a document is checked against the code when the document is touched. No "last updated" footers; the git history is the date.
- Two changelogs, one per family: `com.nexus.core/CHANGELOG.md` covers the core, its adapter packages (`com.nexus.iap.unity`, `com.nexus.firebase`) and the packages split from it (`com.nexus.fsm`, `com.nexus.dots`); `com.nexus.ads/CHANGELOG.md` covers the ad package family (core, providers including `com.nexus.ads.admob`'s `Nexus.Ads.AdMob`, bridge). Each `package.json` is its released version; everything newer is under `[Unreleased]`. A package that has never been released carries 0.1.0 and only an `[Unreleased]` section.
- No performance claim without workload, platform, Unity version, competitor versions and reproduction tool. "Verified" means a result file exists.

**Consumers**
- Games consume the package by git URL pinned to a commit. Framework fixes are made here with a regression test and the game re-pins; a game never keeps an edited copy of the package.
- A game that finds a framework bug, or needs something the framework lacks, opens an issue in this repository first (`gh issue create --repo CevatAYDN/GameContainer`, templates under `.github/ISSUE_TEMPLATE/`). A game session never edits this repository and keeps no unopened drafts. Until the fix ships, a workaround in the game names the issue in its code and is removed at the re-pin that brings the fix. Each game's own rule file carries this rule in these words (#137). Known consumers: Gigawatt, Wickling, ChipCrisis, Formatron, Pixel-Flow-Clone, IdleProject, PuzzleGame, RingFlow (sibling folders under `..\`).

**Changes**
- Every problem and every request starts as an issue in this repository (templates under `.github/ISSUE_TEMPLATE/`); nobody changes the repository or a consumer's pin on their own. Work inside a game (its migration, its re-pin) is an issue in that game's repository; gate 11 in `docs/RELEASE_GATES.md` links it. A change is committed directly on `main` with a message that names its issue; `main` is the only branch in this repository (no feature or pull request branches), and a game pins only commits on `main`.

**Commits**
- Conventional prefixes used here: `fix(di):`, `fix(codegen):`, `fix(lifecycle):`, `feat:`, `docs:`, `test:`, `chore:`. One logical change per commit; the message states what was wrong and how it was verified.
- The pre-commit hook runs the host harness; a commit that fails it is not committed.
