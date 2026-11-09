# Framework findings remediation, 2026-11-09

Scope: GameContainer #344-#352, following the owner's request to reproduce and
close each finding with evidence. This is the remediation record, not the
subsequent whole-framework review (#353).

## Identity and boundaries

- Baseline HEAD: `6bc6cc4321fb53ed5a6209816d3cb2a590b9aebc`, `main`.
- Fixes are in the working tree. No commit, push, consumer re-pin or release was
  performed. Closing a source defect below does not mean the fix is published.
- Both final Unity runs have identical before/after source digest
  `F8016013774C77F3088E5B432B387444DA16682F81363CE97568F1B25D54F438`.
  The runner hashes tracked and untracked `.cs`, `.asmdef`, `.json`, `.rsp` and
  `.txt` inputs under `Nexus/Packages`, `Nexus/Assets` and `Nexus/ProjectSettings`.
- Pre-existing `ProjectAuditorSettings.asset` and `docs/CODE_REVIEW_AUDIT.md`
  were preserved, as were the consumers' changes.

## Finding ledger

| Issue | Defect and resolution | Reproduction / regression evidence |
|---|---|---|
| #344 | Nested typed-history replay overwrote its caller's buffer. Each recursion depth now owns a reusable snapshot, released in finally. | Original sequence dropped the final `2`; `History_NestedReplayAfterClearOrPrune_KeepsOuterSnapshot`, growing recursion, cancellation and warmed allocation checks. |
| #345 | Pool callbacks invalidated live cleanup enumeration and lease ownership. All targeted pools are marked before callbacks, replacement leases stay owned, acquisition/despawn revalidate callback boundaries and final-yield prewarm cancellation is observed. | Shared pool regressions; independent review added cross-existing-pool and returned-acquisition failures. Real Unity timer/activation suite: before 6 passed / 3 failed / 0 skipped; after 9 / 0 / 0. |
| #346 | Initial/older derived computations could overwrite newer source results. Versioned computation and ordered publication retain the newer result without calling user code under the commit lock. | All Select/CombineLatest arities, held concurrent computation, disposal and a callback waiting on another writer; GCA11 retained. |
| #347 | A superseded FSM enter that failed late skipped its exit. The ordinary-exception branch now awaits superseded cleanup. | Supersession, disposal and a held exit before the newest transition; exactly one exit and no stale fallback. |
| #348 | Rollback reused stale derived payloads. External inputs remain recorded; handler-derived payloads, branches and future schedules are regenerated and ordered by corrected production time. Abandoned future snapshots are invalidated. | Changed payload/type/count/tick, partial/pruned replay, typed history, independent chronological reference and zero-allocation checks. The first green implementation was reopened after independent review; later regressions fixed ordering/snapshot/allocation gaps. |
| #349 | The thread-static completed Tick list retained captured save owners. Clear the owned due buffer in finally. | Weak-reference controls, normal/throwing actions, nested Tick and GCA8. |
| #350 | Economy balance and game receipt were separate commits. `EarnOnce`/`EarnBigOnce` adopt one versioned canonical wallet payload containing the balance and retained receipt ids; every subsequent mutation/promotion preserves it and confirmation bypasses throttling. | Historical duplicate after unrelated spending, failed save and abrupt reopen, store override, reentry/concurrency, real encrypted migration failure, commit-before-confirmation interruption, corruption/import and real ads-bridge callback/startup delivery. |
| #351 | The shipped memory store could not confirm saves, express unreadable keys or reopen durable state. Added `TrySave`, `FailNextSaves`, `MarkUnreadable`, `Restore`, `Reopen`. | Initial confirmation regression failed. Six shared host/Unity tests cover live/durable separation, failed saves/deletes, faults/recovery and the real economy guard. The unsupported custom-store default-false test remains intact using an explicitly legacy adapter. |
| #352 | Read-only view bindings had no nonthrowing unreadable-wallet query. Added `TryGetBalance`/`TryGetBigBalance` without relaxing throwing getters or mutations. | Initial fallback threw/caught 20 IOExceptions for 20 probes; native implementation inspects status. A real ViewBinder/Mediator stays bound while unavailable, recovers without rebinding and removes its subscription on unbind. Real encrypted lock/import and warmed 0-byte query checks also pass. |

### Independent-review corrections for #350

1. A custom store callback could confirm a nested grant and then forward an
   older payload: live 160, abruptly reopened 140. Mutations/disposal now reject
   store-setter reentry before state changes; balance-observer reentry remains
   supported. A throwing setter releases the guard and leaves retry possible.
2. Unpaired UTF-16 surrogates were accepted but changed identity during UTF-8
   encoding. Strict validation now occurs before loading or mutating the currency,
   with strict encoding/decoding throughout.
3. The first staging guard could discard a pending network spend's rejection.
   Both long and BigDouble regressions observed 125 instead of 150 with no refund.
   Internal reconciliation now asynchronously awaits the end of staging, then
   refunds and signals once. Real encrypted tests verify durable 150 and retained
   receipt deduplication.

The final bounded independent source review found no remaining actionable
staging/identity/deferral issue. Test execution was performed by the primary
session, not claimed by the reviewer.

## Executed verification

Final evidence folder: `Nexus/artifacts/consumer-persistence-20261109/`.
Earlier red/green evidence for #344-#349 remains in
`Nexus/artifacts/framework-closure-20261109/`; the original baseline is
`Nexus/artifacts/framework-closure-20261109-baseline.log`.

| Check | Result | Current artifact |
|---|---|---|
| Complete .NET host suite | 513 passed, 0 failed | `verified-host.log`, embedded ResultSink JSON |
| Consumer persistence focused suite | 29 passed, 0 failed | `350-refund-green.log` |
| Framework closure focused suite | 35 passed, 0 failed | `345-review-green.log` |
| Unity 6000.6.8f1 EditMode | total 881; 880 passed, 0 failed, 1 skipped | `verified-editmode.xml`, `.log`, `.process.json` |
| Unity 6000.6.8f1 PlayMode | total 303; 301 passed, 0 failed, 2 skipped | `verified-playmode.xml`, `.log`, `.process.json` |
| Actual economy receipt fixture in Unity | 17 passed, 0 failed, 0 skipped | `refund-editmode-focused.xml` |
| Engine-module compile variants | default/editor/Android/iOS; 0 warnings, 0 errors | `verified-compile.log` |
| Documentation | 149 Markdown files, 0 broken links, 0 links to untracked local evidence, before adding this archival record | `verified-docs.log` |
| Version consistency and current upstream lookup | 28 dependency rows, 0 problems | `python tools/check-versions.py --newest`, session command output |
| Diff whitespace validation | exit 0 | `git diff --check`, session command output |

The Unity runner refuses reused evidence names, records PID/native exit/timing,
requires fresh XML and stable sources, and invokes the repository's existing
`tools/unity-verify/check_results.py` for result/count validation. A process exit
or an old XML alone cannot satisfy it.

Skipped tests are not presented as passed:

- EditMode: `LatestCommitEditorRegressionTests.Inspectors_DrawAdditionalNestedSerializedSettings`,
  requires an editor graphics surface.
- PlayMode: `DebugStrippingTests.Trace_WhenDebugSymbolNotPresent_StripsTracingOverhead`
  and `PerformanceTests.SteadyState_HasZeroGCAllocations`, require `NEXUS_DEBUG`
  disabled. The host checks the corresponding non-debug allocation paths.

The host compilation exposed the existing `LifetimeScopeSuite.BrokenPendingResource.Late`
CS0649 test-fixture warning. The real Unity runs and four engine-module compile
variants reported no compiler warnings; this record does not relabel the host
warning as absent.

## Consumer follow-up

- Formatron #9: adopt receipt-keyed triple delivery, remove the balance-snapshot
  workaround and exception-driven wallet query, use the shipped durable test
  store, decide legacy ambiguous-ledger migration, and validate on device.
- ChipCrisis #15: connect direct confirmed reward delivery, fix blocked-delivery
  acknowledgement and startup retry ownership, replace WalletRead and shared
  persistence fake behavior, and validate on device.
- Existing Formatron #8 and ChipCrisis #14/#10 acceptance is preserved. No game
  source or pin changed. Both new tasks explicitly wait for a published full
  framework `main` SHA containing these fixes.

## Guarantees not claimed

- Receipt idempotency is local to one currency and the retained wallet payload,
  not a multi-key transaction with a game blob, another currency or a backend.
  Custom stores must actually provide atomic payload commits and confirmation.
- Legacy ambiguous two-file payments cannot be inferred retroactively. The
  games must preserve/reconcile that migration explicitly.
- The new wallet format is one-way; stale legacy keys are not current balances.
  Receipt ids are retained for the save's lifetime and grow the payload.
- Host coverage compiles 146 of 161 core runtime files; it is not statement or
  branch coverage. Unity/engine compile checks cover additional engine-dependent
  source, but do not establish every SDK/provider/player/device behavior.
- No new native-player build, physical-device run, live ad/store proof, sudden
  power-cut durability or consumer-game acceptance is established by this record.
  Historical device results for `3d53b2f` do not cover the new working tree.
- Whole-framework review remains tracked separately in #353; these closures
  do not mean the entire framework is defect-free.
