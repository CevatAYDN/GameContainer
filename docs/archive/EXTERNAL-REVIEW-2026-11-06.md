# External code review, 2026-11-06

## What was reviewed and how it was proven

External review by GPT 6 Astra (Noxery, through the opencode CLI). Measured commit: `e045f35`
(runtime sources unchanged since `76ba741`). Each of the ten findings was reproduced by a host
proof project compiling the real runtime sources against the harness stubs, and `UnityIapAdapter.cs`
against the project's `Unity.Purchasing.dll`. IAP callbacks were invoked through reflection, with
no store transaction. On `e045f35`, 10 of 10 findings reproduced.

Git-ignored evidence: `Nexus/artifacts/review-external-20261106/` holds the proof (`Program.cs`,
`ReviewProof.csproj`, `REPRODUCE.md`), `results.log` (`e045f35`, 10/10),
`results-60aadc5.log` (`60aadc5`, 1/10), the reviewer's baseline logs
(`nexus-review-*-20261106.*`) and the maintainer's result files listed below.

## Findings and fixes

| ID | Finding | Issue | Fix commit(s) |
|---|---|---|---|
| DI-01 | A scoped instance whose PostConstruct throws is never disposed, also when its container is disposed | #325 | `7170b6c`, `6864c59` (ReInjectAll injected into such an instance) |
| FSM-01 | Caller cancellation while OnEnter ignores the token still commits Success | #322 | `8b6803e` |
| FSM-02 | A throwing cancellation callback stalls every later transition | #321 | `969f30d` |
| SAVE-01 | SaveThrottler dispose during an in-flight save loses the newer save request | #323 | `1b56669` |
| ADS-01 | AdController's generic Show of a banner skips its caps and cooldown | #327 | `2cc82d1` |
| ADS-02 | Withdrawing consent in the privacy options leaves ads running | #328 | `1ce0cbc` |
| IAP-01 | An empty later purchase fetch never removes ownership | #330 | None: open, needs store evidence (owner's decision pending) |
| IAP-02 | A pending consumable missing from the Nexus catalog is owned and confirmed without a grant | #329 | `6bf3f68` |
| UI-01 | A throwing ScreenHistory.Current observer leaves the pushed screen unshown | #326 | `34aa7b6` |
| SAVE-02 | SetString(key, null) does not delete the file of an unreadable key | #324 | `7867f6e` |

Fixes for #321–#329 were written by Claude (Opus 5.5) and its subagents.

## Follow-up issues

Found while reviewing the fixes; new issues drafted by the reviewers and checked by the maintainer:

- #331 (`60aadc5`): AdController ignored IConsentFlow.Changed, so consent changed outside its
  privacy options left ads running or never started them. Harness AdsCoreSuite AC66–AC70;
  with the change reverted AC66, AC67, AC69 and AC70 fail.
- #332 (`4b36c4f`): a catalog consumable with an untyped Google cart was granted and acknowledged
  instead of consumed. With the runtime change reverted, 5 of the new EditMode tests fail:
  `editmode-red332.xml`, 770 total, 764 passed, 5 failed, 1 skipped.

A check of the fixes for remaining limits (by GPT 6.1 Sol, checked by the maintainer) filed three open issues
from static analysis: #333 (SaveThrottler loses a newer request during its disposal retry), #334 (a throwing logger
leaves the FSM transition queue incomplete) and #335 (overlapping harness runs collide on shared temporary files; this collision was observed on 2026-11-07).

#331 was written by GPT 6.1 Sol and #332 by GPT 6 Astra, both through opencode, reviewed and
committed by Claude.

## Verification and outcome

Reviewer baseline on `e045f35`, from `REPRODUCE.md`:

| Check | Result |
|---|---|
| Host harness | 556 PASS, 0 FAIL |
| Compile-check | 0 errors, 0 warnings |
| EditMode | 760 total, 759 passed, 0 failed, 1 skipped |
| PlayMode | 278 total, 276 passed, 0 failed, 2 skipped |

The proof re-run on `60aadc5`, after every fix but #332, reproduced 1 of 10 findings:
IAP-01 only (`results-60aadc5.log`). #330 remains open, pending store evidence and the owner's decision.

Unity results on `4b36c4f` (Unity 6000.6.8f1):

| Check | Result |
|---|---|
| EditMode (`editmode-4b36c4f.xml`) | 770 total, 769 passed, 0 failed, 1 skipped |
| PlayMode (`playmode-4b36c4f.xml`) | 278 total, 276 passed, 0 failed, 2 skipped |
| Compile-check | 0 errors |

The host harness passed in each commit's pre-commit hook.

## What was not verified

No device, store, payment or Android IL2CPP run was performed for any of these fixes.
The DI changes were not run on the Android IL2CPP comparison.
