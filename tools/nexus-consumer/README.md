# Clean UPM consumer gate

Proves that the package works when installed the way a game installs it: packed into a UPM
tarball and imported into a fresh project that has none of the development project's assets,
settings or generated files. This is release gate 8 in `docs/RELEASE_GATES.md`.

```powershell
# from the repository root; Windows, Unity 6000.6.8f1 with the Unity CLI (unity.exe under %LOCALAPPDATA%\Unity\bin)
./tools/nexus-consumer/run.ps1 -RunName consumer-001
./tools/nexus-consumer/run.ps1 -RunName consumer-002 -BuildPlayer -BuildAndroid -ValidatePerformance
./tools/nexus-consumer/run.ps1 -RunName consumer-ads-001 -ValidatePerformance -IncludeAdsBridge
```

What runs, in order (each stage is a separate Unity process; the first failure stops the run):

1. `git archive` packs `Nexus/Packages/com.nexus.core` at `HEAD` into
   `Nexus/artifacts/<RunName>/package/com.nexus.core-<version>.tgz`: the tracked files a game pinned to that
   commit receives, without git-ignored build outputs. The run stops when any packed package has uncommitted
   changes (including untracked files). `-IncludeAdsBridge` also packs `com.nexus.ads`,
   `com.nexus.ads.bridge.nexus` and `com.nexus.firebase`; all four archives use the same captured
   `HEAD` commit, not source copies.
2. `prepare_clean_project.py` creates `Nexus/artifacts/<RunName>/CleanConsumer`: a manifest with
   only the built-in modules, the test framework, `com.unity.pipeline` (which the Unity CLI needs)
   and the tarball, without `testables` as in a game (`-ValidatePerformance` adds core;
   `-IncludeAdsBridge` adds all four packed packages), the development project's
   Unity version, and the smoke assets (sample access helpers, a player probe, editor methods and
   two test assemblies).
3. `consumer-import`: the three samples (Code-first Startup, Counter Example, Nexus Starter Package) are
   imported through the UPM `Sample` API, which also proves the package resolved as a `LocalTarball`.
   None of the three has an asmdef, so all compile into Assembly-CSharp and a compile error in any of them fails
   the project from here on.
4. `consumer-prepare`: a scene with the sample's `ScoreBootstrap` and the probe is created and
   `BuildValidation.ValidateOrThrow()` runs.
5. `consumer-editmode` (`UpmConsumerTests`): the imported Code-first sample equals the archived one byte for
   byte; the Counter and Starter samples are imported and their lifecycles compiled; production architecture
   validation passes with test assemblies excluded; the project's own
   test assembly, referencing `com.nexus.core.testing`, uses `NexusTestHarness`, `RecordingSignalBus`,
   `InMemoryPlayerPrefsService`, `GcAllocationProbe` and the mock ad adapter's `RewardOutcome`.
6. `consumer-playmode` (`CodeFirstConsumerTests`): one component starts at score 1 and destroying
   its owner disposes the container; the Counter sample's lifecycle starts on a context with strict
   injection (a new ContextData's default) and resolves its model.
7. `-ValidatePerformance`: the package's own performance, trace-stripping, hardening, readiness,
   service, reentrancy and lifecycle suites run inside the consumer, followed by separate context-hierarchy
   shutdown, root-startup failure, signal-bus, unreadable economy-balance and encrypted-save confirmation stages (filters below).
8. `-BuildPlayer`: a Windows Mono development player is built and executed headless; the probe
   writes `player-mono-result.json` with `passed: true` only when startup reached score 1 and
   disposal threw on resolve.
9. `-BuildAndroid`: an Android ARM64 IL2CPP development APK is built (build evidence only; it is
   not installed on a device). The stage fails unless the build log says the core's
   `NexusPackagesLinkXml` handed at least one package `link.xml` to the linker; the count is recorded as
   `packageLinkXmlFiles` (#115).

`-IncludeAdsBridge` runs after the core tests and before optional builds, independently of
`-ValidatePerformance`. The generated game's `AdsBridgeConsumerTests` references the public core,
ads and bridge assemblies directly, checks all four packages are `LocalTarball`, and calls
`NexusAdsBridge.Connect`/`Attach` to verify invalid connections are rejected. The bridge's own
SDK-less contract, durable reward and PlayMode suites run from the installed archives.
The Nexus Firebase adapter supplies the `com.nexus.firebase` assembly referenced by the
bridge's editor tests and enables `Nexus.Ads.Bridge.Nexus.Firebase`. Its `package.json`
depends only on `com.nexus.core`; Google Firebase SDK adapters are conditional on SDK version
defines. The tooling regression check verifies that manifest dependency remains SDK-less.
No Google Firebase SDK, ad provider SDK, account, real ad traffic or device run is added.

Added filters are separate stages so a missing fixture cannot hide behind another suite's results:

| Switch | Stage | Mode | Filter |
|---|---|---|---|
| `-ValidatePerformance` | `consumer-context-hierarchy-shutdown` | PlayMode | `ContextHierarchyShutdownTests` |
| `-ValidatePerformance` | `consumer-root-startup-failure` | PlayMode | `RootStartupFailureTests` |
| `-ValidatePerformance` | `consumer-signal-bus` | EditMode | `SignalBusTests` |
| `-ValidatePerformance` | `consumer-economy-unreadable-balance` | EditMode | `EconomyUnreadableBalanceTests` |
| `-ValidatePerformance` | `consumer-encrypted-storage-commit` | EditMode | `EncryptedStorageCommitTests` |
| `-IncludeAdsBridge` | `consumer-ads-public-api` | EditMode | `AdsBridgeConsumerTests` |
| `-IncludeAdsBridge` | `consumer-ads-bridge-contract` | EditMode | `NexusAdsBridgeContractTests` |
| `-IncludeAdsBridge` | `consumer-ads-durable-reward` | EditMode | `DurableRewardDeliveryTests` |
| `-IncludeAdsBridge` | `consumer-ads-bridge-playmode` | PlayMode | `NexusAdsBridgePlayModeTests` |

`DurableRewardDeliveryTests` (#338) and `EconomyUnreadableBalanceTests` (#339) are coordinated
fixture names: commit their implementations before running their switches. Every test stage
still requires a result XML with at least one test and overall `Passed`; no fixture is skipped
when missing. The default core-only workflow and its existing stages remain unchanged.

Evidence: `consumer-results.json` (stage exit codes), the test XMLs, editor and CLI logs with
access tokens redacted, `consumer-preparation.json` and `source-identity.json` (commit, tarball
SHA-256, working-tree diff). The folder is git-ignored; a run never reuses an existing folder.
The existing core `archive`/SHA fields remain; bundle runs additionally record a `packages`
array with each archive's SHA-256, and `source-identity.json` records their common commit.
The gate refuses completion if `HEAD` changed during the run.

Tooling-only regression check (temporary projects only; does not launch Unity):

```powershell
python -I -B -m unittest discover -s tools/nexus-consumer -p test_prepare_clean_project.py
```

For direct preparation, keep `--archive <core.tgz> --output <folder>` and optionally `--testables`.
Bundle preparation additionally requires `--ads-archive <ads.tgz>`,
`--ads-bridge-archive <bridge.tgz>` and `--firebase-archive <firebase.tgz>` together.
`run.ps1` proves their shared commit; the Python helper
validates file availability, not Git provenance.
