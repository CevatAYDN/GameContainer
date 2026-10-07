# Nexus current commit review and improvement evidence — 7 October 2026

Reviewed current commits `8c91403`, `94beec3`, `21a0b7b`, `3cd154b` and preserved
their new runtime/editor capabilities. Fixes remain in the working tree; no commit,
push or publication was performed. Scope: Unity 6 + UPM, mobile and PC foundation.
Nexus is an architecture framework; it does not supply every game's mechanics,
art/assets, platform provider SDKs, multiplayer backend or production acceptance.

| ID | Confirmed problem | Resulting contract | Verification |
|---|---|---|---|
| LC01 | Input System assembly reference had no UPM dependency | Declared com.unity.inputsystem 1.20.0; immutable consumer resolved depth 1 without a direct manifest dependency | Fresh consumer import/build |
| LC02 | ContextData inspector used non-existent lowercase property names | Bind actual serialized fields | SerializedObject bindings and Undo |
| LC03 | Custom inspectors hid strictness/dependencies/pool/timeout/inherited settings | Draw remaining serialized properties | Actual IMGUI nested property rendering |
| LC04 | Global menu parented root under selected scene object | Keep Global Root top-level | Menu hierarchy and live creation |
| LC05 | Menu activated scopes before setting parent/config | Configure inactive GameObjects before Awake | Real PlayMode Context.Parent/data assignment |
| LC06 | Binding preview included self for Children and ignored custom overrides | Match runtime target rules | Six preview-vs-injection cases |
| LC07 | Initialization priority tooltip contradicted runtime ordering | Higher priorities first | Source and existing ordering suite |
| LC08 | Behaviour could lose early injection/subscriptions and retain disposed context | Wait for configured ownership; defer Start until async initialization; clear/rebind once | Actual early Awake, disposal and replacement |
| LC09 | Serialized global discovery depended on Awake order | Discover flagged global during cold startup before parent linkage | Scene-root Awake before global Awake |
| LC10 | Duplicate globals silently overwrote ownership | Reject duplicates; preserve first owner | Duplicate registration and unchanged owner |
| LC11 | Big currency migration used old disk values and split ledgers; null GetBalance threw | Promote cached live balance to one canonical Big ledger; retain long observable identity/projection | Throttle/restart/mixing/null/reentrant observable cases |
| LC12 | Big transactions bypassed network validator and accepted invalid numbers | Optional Big validator capability; unsupported mutations fail closed; finite/nonnegative amounts | Backend rejection/refund/disposal and malformed values |
| LC13 | Neutral custom input fell through to stale joystick/keyboard | Provider owns zero as well as movement; clamp magnitude | Neutral provider and clamping |
| LC14 | DOTS bridge could read/dispose while jobs still wrote | Register JobHandles and complete before drain/reinitialize/destroy; main-thread bridge Enqueue | Three real parallel job tests |
| LC15 | Old prefs Big defaults discarded writes; concrete encoders risked precision/culture loss | Shared finite-only invariant R codec using existing string storage | Legacy adapter and real Unity PlayerPrefs round-trip/rejected writes |
| LC16 | Ready observer could dispose owner yet startup returned success and later observers ran | Stop observers once disposed; check startup cancellation/aliveness after notification | Disposing configured/initialized observers |
| LC17 | Nested context startup could inject/Awake twice or start a stale attachment | Guard injection, version attachments, unwind before reattachment; Binding unsubscribes before injection | PostConstruct/event/manual reentry, swap and teardown |
| LC18 | Failed global blocked replacements and remained in sibling wait registry | Release global/sibling ownership; exclude failed roots from cold discovery | Actual failed startup followed by initialized replacement |
| LC19 | Setting Context=null after disposal did not clear explicit mode | Include explicit ownership state in same-reference setter guard | Dispose, clear explicit ownership, automatic replacement |
| LC20 | Disabled legacy backend/invalid named actions could be probed repeatedly | Compile-time backend selection and failed-action cache; clarify ready-injection inspector label | Native compilation and provider regressions; enabled legacy action error caching is source-reviewed, not hardware input acceptance |

Additional improvements implemented earlier in this work and retained by the new
commits: ownership-aware named/default DI validation and eager-cycle diagnostics,
consumer factory/parameter preservation, idempotent loading-time signal/command
prewarm, active lease duplicate ownership guards, safe closed-reference Mono/JIT
constructor delegates, immutable-UPM locale/guide discovery, and EN/TR adoption
paths. Open-generic constructor emission caused a native Mono assertion during the
first experiment; the guard was corrected and covered before the passing runs.
That crashed attempt is retained as superseded evidence, not a PASS.

## Verified evidence

- Licensed Unity 6000.5.6f1 EditMode: **352 passed / 0 failed / 0 skipped**, with a
  graphics surface. Current runtime/editor compilation: no C# warnings/errors.
- PlayMode: **160 passed / 0 failed / 2 skipped**. The two debug-off assertions are
  intentionally skipped in the main debug configuration; fresh consumer checks below
  execute their applicable configuration.
- Canonical repository-root .NET host harness: **291 passed**. Source generator output
  is compiled and booted; Unity APIs are stubbed in this harness.
- Fresh immutable tarball consumer: **15 successful stages / 107 passed test cases**.
  Real UPM sample import, production architecture validation, score startup/disposal,
  10 debug-off performance checks, trace stripping, prune allocation, 41 hardening,
  24 readiness, 13 services, 8 reentrancy and 6 native lifecycle cases all passed.
- Input System resolved transitively at depth 1; the clean manifest does not directly
  include it. Optional Collections is absent in this consumer; the core still builds.
  Main-project optional DOTS tests separately use Collections 2.6.8 and real jobs.
- Windows x64 Mono **development** player built and ran the real code-first sample;
  startup reached score 1 and owner destruction made DI resolution throw disposed,
  recorded as `{"passed":true}`.
- Android ARM64 IL2CPP **development** APK built. ZIP contains arm64-v8a/libil2cpp.so
  and no armeabi-v7a library. This is build evidence, not Android device execution.
- Tarball metadata/sample-guide checks passed: 738 entries, 351 unique GUIDs, 3 samples.
  Every archived package file was byte-compared against the final working-tree package.
- Final independent read-only review found the reported lifecycle/reentrancy issues
  closed; it did not substitute for or re-run native tests. `git diff --check` is clean.

Exact tested tarball: `com.nexus.core-0.4.0-755c974da82a.tgz`

SHA-256: `755c974da82af2143cb0f8f0bf1b08e1107769be7a82a5c834da918b8a31f715`

Local authoritative evidence:

- [EditMode results](../Nexus/artifacts/excellence-20261007/release-final-editmode.xml)
- [PlayMode results](../Nexus/artifacts/excellence-20261007/current-source-playmode.xml)
- [Consumer stages](../Nexus/artifacts/excellence-20261007/current/consumer-results.json)
- [Windows player result](../Nexus/artifacts/excellence-20261007/current/player-mono-result.json)
- [Android build log](../Nexus/artifacts/excellence-20261007/current/consumer-build-android-il2cpp-editor.log)
- [Host regression log](../Nexus/artifacts/excellence-20261007/final-host-regressions.log)
- [Raw comparison rounds](../Nexus/artifacts/excellence-20261007/comparative-reproducible-results.json)

Artifact paths are local/ignored evidence. CLI access tokens were removed from owned
logs before reporting. Unity shutdown printed a native temporary-stack allocator
message; no C# compiler/build errors or failed checks occurred. This message alone
has not been attributed to Nexus or treated as device memory acceptance.

## Performance comparison and limits

The pinned repeatable comparison uses real core DI implementations on .NET 10.0.12,
Windows x64, Release, no source generation/reflection baking for any candidate.
VContainer: 1.19.0 / `5401e5a7ebc4980a2b82141ffc26391a6547edd7`.
Zenject: 9.2.0 / `c2e33500a84f9408a809deca2af2c55494ab2482`.
Same readonly three-node graph; values/lifetimes asserted; 4,000 warmups and seven
rounds with rotated order. Metadata is warm for registration/build. Common wrappers
and available disposal are included; Zenject core exposes no disposal operation.

| Workload | Nexus ns / B | VContainer ns / B | Zenject ns / B |
|---|---:|---:|---:|
| Cached resolve | 10.51 / 0 | 35.42 / 0 | 241.65 / 0 |
| Transient three-node graph | 587.97 / 72 | 222.45 / 72 | 1301.33 / 432 |
| Register/build/first resolve, warm metadata | 4079.30 / 10984 | 3241.70 / 5920 | 4960.60 / 6216 |

Nexus leads cached resolves in this run. VContainer leads transient creation and
startup; Nexus startup allocates more than both competitors. Suitable constructor
emission reduced the earlier same-graph Nexus allocation from 160 to 72 B (55%);
there is no demonstrated latency improvement from that change. Transient objects
still allocate. These are host microbenchmarks, not Unity/IL2CPP or game frame claims.
[Reproduction tool](../tools/nexus-comparison/README.md),
[package performance guide](../Nexus/Packages/com.nexus.core/docs/PERFORMANCE_COMPARISON.md).

## Integration docs and remaining acceptance

[English integration paths](../Nexus/Packages/com.nexus.core/docs/INTEGRATION_PATHS.md),
[Turkish integration paths](../Nexus/Packages/com.nexus.core/docs/INTEGRATION_PATHS_TR.md),
[getting started](../Nexus/Packages/com.nexus.core/docs/GETTING_STARTED.md),
[migration contracts](../Nexus/Packages/com.nexus.core/MIGRATION.md),
[architecture](../Nexus/Packages/com.nexus.core/docs/ARCHITECTURE.md) and changelog were
updated to match code. Setup locales and resolved guide paths work in the tarball.

BigDouble is approximate game-number arithmetic; long views truncate/saturate after
promotion. Mutable observables are trusted client-code surfaces. Local obfuscation
and optional validation do not provide server authority. Main-thread context and
DOTS producer ownership rules are explicit. Update code requiring initialized
services waits for the component's Start hook.

Remaining proof needed before calling this universally superior or production
accepted: native competitor comparison with generated bindings, cold full-context
startup/memory and real mobile/PC frame p95/p99; physical Android/iOS runs and storage
interruption; Windows IL2CPP; real provider/network integration; novice usability
trials and representative genre games. Unity 6000.0 API branches have not been run in
a separate 6000.0 editor. Current licensed execution is 6000.5.6f1. Next performance
work should target transient resolve/build allocation and compare native workloads.
