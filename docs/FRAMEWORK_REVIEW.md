# Framework Review: Multi-Game Reuse

Tracking: [GameContainer #353](https://github.com/CevatAYDN/GameContainer/issues/353).
This is the live record for the owner's 2026-11-09 request, not a release certificate.
Base HEAD: `6bc6cc4321fb53ed5a6209816d3cb2a590b9aebc`.
The fixes are working-tree changes; no commit, push or consumer re-pin has been performed.

## Acceptance Boundary

Source review, host assertions, native Unity tests, SDK-wrapper tests, player builds,
physical-device observations and live store/provider acceptance are different evidence.
A passing count does not prove every possible execution or every future game correct.
An issue is closed only for its stated, reproduced behavior; unpublished source is
identified explicitly in its closure comment.

The earlier #344-#352 remediation is recorded in
[the immutable remediation report](archive/REMEDIATION-2026-11-09.md).
Those earlier counts are not the current full-review counts.

## Review Coverage

| Area | Reviewed Boundaries | Executable Verification |
|---|---|---|
| DI and composition | Scope/alias ownership, failure cleanup, lazy owner, view acquisition, context/lifecycle teardown | Shared composition regressions, native activation/disposal tests, existing lifecycle/DI suites |
| Dispatch and reactive state | Commands, retry/fallback/timeout, cancellation, subscriptions, queues, tracing, observables, FSM, replay/rollback | Shared regressions, stress/fuzz/zero-allocation checks, native EditMode/PlayMode |
| Persistence and economy | Confirmed saves, wallet/receipt identity, unreadable data, progression, deferred saves, deletion races, offline clocks | Real isolated files, deterministic interleavings, reopening test stores, native serialization tests |
| Unity services | Pools, canvas ownership, audio lifetime/gain, metrics, scene/input/UI/service integration | Native engine-object tests plus existing service and runtime suites; host stubs are not engine proof |
| Optional packages | Ads/controller/consent/providers, IAP, Firebase, Play services, DOTS and notifications | Pinned SDK snapshots and controlled callbacks where listed below; live-store/device gaps remain explicit |
| Editor and tooling | Both binder generators, source/shipped analyzer parity, source guards, output migration, validators, build identity and runner acceptance | Emitted-code compile/boot/clear, native editor tests, isolated real-Git and runner fixtures |

This matrix describes review and test coverage, not exhaustive branch coverage.
Detailed triggers, owning files and per-finding proof remain in the linked issues.

## Finding Ledger

The local evidence root is `Nexus/artifacts/framework-full-review-20261109/`.
`issues-composition.json`, `issues-signals.json`, `issues-unity-services.json`,
`issues-ads.json`, `issues-platform.json` and `issues-storage.json` map review codes
to individual issues. Source candidates were not counted as bugs until reproduced.

| Issues | Correction Or Remaining Boundary | Evidence |
|---|---|---|
| #354, #401, #408 | Binder condition guards, safe output retirement, array/identifier/cache/literal emission | Native conditions; filesystem regressions; CodeGen21 and source/DLL parity |
| #355, #400, #409 | Honest text-triage labels; dirty build identity; cold ADB initialization before strict state validation | Real-Git fixtures; verification-runner15; actual current Android build sidecar; comparison-runner73 scenarios |
| #356, #359, #362, #365, #368, #371, #373, #375, #377 | DI/context/view ownership, failure cleanup, aliases and lazy lifetimes | Composition30 host cases, native counterparts and completed IL2CPP DI comparison |
| #357, #361, #364, #367, #370, #372, #374, #376, #378, #379 | Retry/timeout/fallback, cancellation cleanup, async depth, retained payloads, queue generations and trace ownership | Signals20 shared cases; native collection controls and existing regression suites |
| #358, #360, #363, #366, #369 | Retiring canvases, activation-time leases, shared audio runners, live gains and unscaled metrics | Eleven targeted native Unity service cases; old pool-root checks strengthened |
| #380, #386, #392, #394, #396, #397, #398, #399 | Delivery receipt identity, pause restoration, ad-service lifetime, XML/Gradle/name validation and pacing | Host/native controller, service, validation and lifecycle fixtures |
| #388 | Resources default cannot authorize an invalid serialized scene override | Actual native scene plus real Android report input; both build hooks tested, including custom scene-list case |
| #390 | Provider resources and asynchronous work end with their actual owner; controller borrows ports | AdMob/LevelPlay controlled SDK fixtures and bootstrap consent-owner regression |
| #381, #387 | Requested IAP failure remains deliverable; direct service disposal releases its adapter | SDK5.5 delivery/confirmation tests, existing IAP tests and native service test |
| #383 | A DOTS handler's new producer is completed before the next dequeue | Actual native job/queue regression |
| #385, #393, #395 | Firebase continuation lifetime, denied properties and removed-key notifications | Real SDK14.1 service logic with controlled SDK I/O; 15 targeted and 131 existing tests |
| #389 | Terminal update status ends the wait; late failed/accepted activity results are classified correctly | Actual SDK1.8.3 managed request objects; 6 targeted and 9 existing tests |
| #402, #403, #404, #405, #406, #407 | Fail-closed progression/clock reads, path/query failures, retired writes, capture ordering and stale negative cache | Storage25 host regressions and native cases, including observer-disposal and lock-order follow-ups |
| #382 | Opt-in `RequestRewardAsync(placement, grant, ct)`: grant runs inside the callback before acknowledgement; a throwing grant faults the task and keeps the receipt; legacy overload unchanged | Four new host regressions (`A02_GrantOverload*`) plus the two legacy characterizations; full host run passed |
| #384 | Managed callbacks preserve show identity, but delivery after native handle destruction is not established | Controlled late-callback regressions pass; actual provider/device boundary OPEN |
| #391 | One process-wide SDK manager (no handler accumulation); a status-only terminal request is ended through the SDK's own error path so the next start is accepted | `sdk-googleplay-p06-red-01`: 3 expected failures on the baseline runtime; `sdk-googleplay-p06-green-01`: 18 passed / 0 failed, 0 CS diagnostics (SDK appupdate1.8.3) |
| #410 | Not a Nexus defect: Unity's own `com.unity3d.player.PlayAssetDeliveryUnityWrapper` probes the optional class by reflection and logs the `ClassNotFoundException` when Play Asset Delivery is absent | `410-empty-unity-probe`: an empty Unity6000.6.8f1 IL2CPP project with no Nexus package logs the same line on the same phone; string evidence from the engine's `classes.jar` |

## Integrated Results

| Check | Result | Evidence |
|---|---|---|
| First integrated host checkpoint | 624 passed / 0 failed; 0 compiler warnings/errors; stable inputs | `full-host-01.results.json`, `.process.json`, `.inputs.json` |
| Final host | 624 passed / 0 failed; 0 compiler warnings/errors; stable inputs | `full-host-02.*` |
| Full native EditMode | 990 passed / 0 failed / 1 explicit skip; 0 compiler warnings/errors | `Nexus/artifacts/consumer-persistence-20261109/fullreview-editmode-full-02.*` |
| Full native PlayMode | 319 passed / 0 failed / 2 ignored; 0 compiler warnings/errors | `Nexus/artifacts/consumer-persistence-20261109/fullreview-playmode-full-02.*` |
| Engine-module compile | Release/editor/Android/iOS variants: 0 warnings, 0 errors | `compile-variants-final.log` |
| Tool tests | Verification runner15, consumer preparation6; text triage3 from its red/green record | Runner logs and command output; not Unity/player evidence |
| Current dependency declarations | 28 rows, 0 problems, current upstream lookup included | `versions-newest-final.log` |
| Player/physical DI acceptance | Mono player and Android ARM64 IL2CPP reflection comparison completed | `di-device/fullreview-reflection-01`, `di-device/fullreview-reflection-device-02` |
| Current development-player build identity | Android IL2CPP development build succeeded; fresh APK/modified sidecar, stable source inputs | `android-player-final/build.process.json` |

The explicit EditMode skip is the build-hook test run separately in the isolated
`sdk-ads-buildguard-green-01` project: 1 passed, 0 failed. Its old Android BuildReport
is test input, not a new Android build.

The two PlayMode skips require `NEXUS_DEBUG` off: tracing stripping and steady-state
zero-allocation dispatch. The host checks production dispatch without that define.
This is not a claim that debug tracing allocates nothing.

After the Android build, a fresh PlayMode run restored the Win64 test configuration
and verified the same final inputs as EditMode. Both final source digests are
`3F4654BFA9ACFF7D3F119D750755A9020FA5863A17CBBF019EBE30FDE4397202`.
The native runner hashes tracked and untracked non-ignored C#/assembly/configuration
inputs, including analyzer DLLs; generated `.meta` files are excluded from that digest.

The physical DI run used Unity6000.6.8f1, Android16 on model2311DRK48G, a
non-development ARM64 IL2CPP player with no generated bindings/debug tracing.
Nexus medians were 28.645ns cached resolve, 4119.75ns for a transient three-node
graph, and 14868.7ns for register/build/first resolve with warmed metadata.
These are that workload's latency measurements, not full-context startup, game FPS,
an across-device comparison or a universal performance lead. The device allocation
counter was invalid, so no device zero-byte claim is made.

APK SHA256: `902E73B407DEBAF34D78EEC35E1883F93AFD758E0DC90E62720A5DDE19B17D85`.
The installed hash was checked before/after, the actual completion marker/PID was
captured and the owned process was confirmed stopped. The run paused/resumed, and
startup logged a missing `com.google.android.play.core.assetpacks.AssetPackManager`
class. Measurements completed, but this is not a zero-error player-health verdict.
Its attribution is tracked separately in #410.

The current development APK is
`Nexus/artifacts/framework-full-review-20261109/android-player-final/Nexus-fullreview-development.apk`,
SHA256 `D18E7CD1DBB666618C7E901F2BB1872F1DF110C14253F86EA3F0C26E315C5FE1`.
The sidecar correctly says `6bc6cc4321fb53ed5a6209816d3cb2a590b9aebc (Packages or Assets modified)`.
The previous APK/sidecar were copied into the evidence directory before building.
The development APK was built, not installed or accepted on the phone.

Build diagnostics are not collapsed into a zero-warning claim: CS/UAC compiler
diagnostics were zero; Unity's BuildReport counted three warnings. The log names
unlinked Unity Cloud/IAP license configuration and mobile pixel-light settings,
and clang also reported unused `-x c++` argument warnings. The demo's test ads
validation reported two warnings and no failure. These are not verified release
configuration or game rendering/performance acceptance.

## SDK Evidence

All projects below are immutable working-source snapshots with verified package input
hashes and resolved SDK versions. They are not published Git pins.

| SDK | Targeted | Existing Tests | Snapshot |
|---|---|---|---|
| Unity IAP5.5.0 | 2 passed | EditMode15, PlayMode12 | `sdk-iap-green-01` |
| Firebase14.1.0 | 15 passed | EditMode131 | `sdk-firebase-green-01` |
| Google Play appupdate1.8.3 | 6 passed | EditMode9 | `sdk-googleplay-green-04` |
| Google Mobile Ads11.6.0 | 5 passed | EditMode7, PlayMode8 | `sdk-admob-green-01` |
| LevelPlay9.6.0 | 2 passed | EditMode3, PlayMode11; bootstrap lifecycle3 | `sdk-levelplay-green-02` |

Each listed completed SDK run has zero failed/skipped/inconclusive tests and zero
CS compiler diagnostics. Controlled clients replace network/store I/O, not the
adapter's decision, conversion, buffering or ownership logic. No ad impression,
real purchase, live Firebase collection, consent compliance or store update was
performed to obtain these results.

## Invalid Attempts Kept

- The first existing IAP EditMode filter used the wrong namespace and discovered zero tests.
- Initial signal GC controls stayed alive themselves; their results were invalid until
  lifetime controls were corrected without removing the retention assertions.
- Unsupported NUnit attributes blocked one native checkpoint; they were removed consistently.
- The first Google Play fix spun on inline cancellation of its delay. That owned process
  was stopped and its run remains UNVERIFIED. A cancellation check fixed it.
- The late accepted-dialog counterpart exposed a false failure after a successful download;
  its red and corrected green are retained.
- Build-hook fixture attempts initially failed on scene setup/native asset lifetime, then
  on an unrelated new SDK-test constructor. Those are not product reds. The actual missing
  build exception was reproduced only after the setup controls passed.
- The offline-time fixture subtracted one hour from a fresh Mono counter:
  recorded13,372ms became -3,586,628ms. The production guard correctly refused it. The fixture
  now observes a positive real interval and checks that recording another slot cannot
  reduce it; no negative-counter protection or assertion was removed.
- The first comparison launch rejected daemon startup lines plus a valid `device`
  reply. The retained transcript shows exit0, not an unauthorized phone. A fresh
  launch-only run completed. #409 adds explicit daemon startup before strict state
  validation; its cold regression failed before the fix and the full device-free
  suite passed874 assertions across73 scenarios afterwards.

## Consumer Handoff

- [Formatron #9](https://github.com/CevatAYDN/Formatron/issues/9) tracks atomic triple
  delivery, wallet-query and testing-store adoption, migration and its own device acceptance.
- [ChipCrisis #15](https://github.com/CevatAYDN/ChipCrisis/issues/15) tracks reward-receipt
  integration, WalletRead removal, persistent replay, migration and its own device acceptance.

Both wait for a published full `main` SHA matched to final evidence. Neither
`3d53b2fefd09efc172aeb86da78bd972852d679d` nor the unchanged base HEAD is that target.
The framework tests do not close either game's phone, store or notification-tap acceptance.
The issues were supplemented with the full-review callback/consent ownership,
binder/build validation and, for ChipCrisis, actual offline-clock consumer paths.

## Post-Commit Verification (`1fee270d`, pushed to `main`)

| Check | Result | Evidence |
|---|---|---|
| Host (pre-commit hook) | ALL BENCHMARKS / CODEGEN PASSED | commit hook output |
| Full native EditMode | 994 passed / 0 failed / 1 skip; 0 CS diagnostics | `framework-closure-20261109/final-1fee270-editmode.*` |
| Full native PlayMode | 319 passed / 0 failed / 2 skips; 0 CS diagnostics | `framework-closure-20261109/final-1fee270-playmode.*` |
| #384 LevelPlay device probe | Player built against `1fee270d` and started `mode=Provider testMode=on`, but every load answered `509 Mediation No fill`; no ad could be shown, so the native callback-after-destroy boundary is still unobserved | `device-384-levelplay/` |

## Closure Status

The full-review fixes #354-#409 are closed except #384:
#382 and #391 were resolved in a follow-up with red/green evidence, and #410 was
attributed to the Unity engine. Overall #353 remains open while #384's native
callback-after-destroy boundary is unestablished.
Closing local fixes does not publish them.

## Preservation

Pre-existing owner files remain byte-identical:

- `Nexus/ProjectSettings/ProjectAuditorSettings.asset`:
  `4071EBDD9BCF64A42402E7B284D62E29D6DDBEA18549FDBE92EF78D1AFC4F37A`
- `docs/CODE_REVIEW_AUDIT.md`:
  `AE3CCA573718B96F88A5FA8C80F862EC6C691E5BDD831E6276BD73964D16952E`

No game repository sources/pins were edited, and no game data was uninstalled or reset.
