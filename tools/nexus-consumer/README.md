# Clean UPM consumer gate

Proves that the package works when installed the way a game installs it: packed into a UPM
tarball and imported into a fresh project that has none of the development project's assets,
settings or generated files. This is release gate 8 in `docs/RELEASE_GATES.md`.

```powershell
# from the repository root; Windows, Unity 6000.5.6f1 with the Unity CLI (unity.exe under %LOCALAPPDATA%\Unity\bin)
./tools/nexus-consumer/run.ps1 -RunName consumer-001
./tools/nexus-consumer/run.ps1 -RunName consumer-002 -BuildPlayer -BuildAndroid -ValidatePerformance
```

What runs, in order (each stage is a separate Unity process; the first failure stops the run):

1. `NexusVerifyBuild.PackPackage` packs `Nexus/Packages/com.nexus.core` with the Package Manager
   into `Nexus/artifacts/<RunName>/package/*.tgz`.
2. `prepare_clean_project.py` creates `Nexus/artifacts/<RunName>/CleanConsumer`: a manifest with
   only the built-in modules, the test framework, `com.unity.pipeline` (which the Unity CLI needs)
   and the tarball, without `testables` as in a game (`-ValidatePerformance` adds them), the development project's
   Unity version, and the smoke assets (sample access helpers, a player probe, editor methods and
   two test assemblies).
3. `consumer-import`: the Code-first Startup sample is imported through the UPM `Sample` API, which
   also proves the package resolved as a `LocalTarball`.
4. `consumer-prepare`: a scene with the sample's `ScoreBootstrap` and the probe is created and
   `BuildValidation.ValidateOrThrow()` runs.
5. `consumer-editmode` (`UpmConsumerTests`): the imported sample equals the archived one byte for
   byte; production architecture validation passes with test assemblies excluded; the project's own
   test assembly, referencing `com.nexus.core.testing`, uses `NexusTestHarness`, `RecordingSignalBus`,
   `InMemoryPlayerPrefsService`, `GcAllocationProbe` and the mock ad adapter's `RewardOutcome`.
6. `consumer-playmode` (`CodeFirstConsumerTests`): one component starts at score 1 and destroying
   its owner disposes the container.
7. `-ValidatePerformance`: the package's own performance, trace-stripping, hardening, readiness,
   service, reentrancy and lifecycle suites run inside the consumer (this switch writes `testables` in the manifest).
8. `-BuildPlayer`: a Windows Mono development player is built and executed headless; the probe
   writes `player-mono-result.json` with `passed: true` only when startup reached score 1 and
   disposal threw on resolve.
9. `-BuildAndroid`: an Android ARM64 IL2CPP development APK is built (build evidence only; it is
   not installed on a device).

Evidence: `consumer-results.json` (stage exit codes), the test XMLs, editor and CLI logs with
access tokens redacted, `consumer-preparation.json` and `source-identity.json` (commit, tarball
SHA-256, working-tree diff). The folder is git-ignored; a run never reuses an existing folder.
