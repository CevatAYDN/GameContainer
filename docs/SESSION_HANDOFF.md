# Session handoff

## Current continuation — 2026-11-24 (audit fixes)

An external audit was checked against the code; its verified defects became #447-#457. #447, #448, #449 and #451-#456
are fixed, reviewed twice and pushed (`9762a194..b812b1f4`, gates in `RELEASE_GATES.md`, top); all of them are closed. The refuted audit claims
are listed in `Nexus/artifacts/review-handoff-20261124.md`, so a later audit does not reopen them. Still open:
- #450: measured. There is no cap on the number of receipts, because the README promises lifetime retention. The fix to make is to cache the encoded receipt segment.
- #457: iOS device checks, asked of the games.
- #436: a comment notes that `InfoPlist.strings` should be merged, not overwritten. The four games use their own iOS post-process, so it does not touch them.

The four games pin `8d5fb5b9`, and their re-pin to `b812b1f4` is Gigawatt#12, Wickling#14, ChipCrisis#26 and Formatron#17. A build that is in App Review is not changed: the re-pin goes into the next upload.

## Earlier continuation — 2026-11-24 (game re-pin)

All four games were re-pinned to `46aa72b6`, and each game's `main` (Android) was fast-forwarded to its `main-ios` (iOS):
Formatron `b0122e2`, ChipCrisis `441dd074`, Wickling `9807ff1`, Gigawatt `92c5b4d`. This was done while ChipCrisis build 5
and Gigawatt 10006 were in App Review, which the earlier rule ("games re-pin one at a time, not while one is under review")
forbade. It was the owner-requested consistency pass. If review rejects a build, fix it from the commit that build was made
from, not from the tip.

Cross-game ads standard: LevelPlay 9.5.1 with native 9.8.0, UnityAds adapter 5.18.0.0, AdMob adapter 5.35.0.0, identical
`LevelPlayVersions.json` (blob `c9c80ed`); UMP consent; rating G; Test tab = Google samples.
Open: #445 (two core editor tests fail inside Gigawatt), #443, #428, #434-#436 device proofs. No device run was made.

## Earlier continuation — 2026-11-24 (#444)

#444 is done: with `Consent: GoogleUmp`, Google's US-state opt-out (GPP usnat v1/v2, usca; `IABUSPrivacy_String` as fallback) reaches
LevelPlay `do_not_sell` when UMP answers and when its answer changes (`7bb6e960`, review fixes `fed7d746`; evidence in
`RELEASE_GATES.md`, top). Games re-pin to the pushed SHA that carries these commits, one at a time. Not run on a device.

## Earlier continuation — 2026-11-23 (#443)

Guideline 5.6 follow-up (#443). The ten Mac commits `fc5bcc60..60715202` and the four local fixes (`48d35a81`,
`da3d367a`, `9786c669`, `9d845a9d`) are verified on Windows (`RELEASE_GATES.md`, top) and pushed to `origin/main`
with the owner's approval (`edd61bce`). Closed: #437, #438, #439, #440, #442. #434, #435 and #436 stay open for device
proofs (listed in #443's comment); #429 stays open. Games re-pin one at a time, not while one is under review.
No device run was made in this session.

## Earlier continuation — 2026-11-19 (#433, #429, #422, #431, #432, #428)

Pushed to `origin/main` with the owner's approval after the full suites on `ff533371` (`RELEASE_GATES.md`, top):
#433 (`4dc286ad`, `465fa448`, `710a3154`), #429 dependency proofs (`99c4c0b3`, `5b0fcc5d`, `e738788c`, `3bd2783c`),
#422 `PendingRewards` (`530ff932`), #431 per-platform consent, `AndroidConsent` / `IosConsent` (`63731e6c`, `bc51d754`),
#432 Google's flow answering once UMP's status update allows ad requests (`77e48fac`, `e607a147`) and the #428 live
batch proof (docs only). Closed: #433, #422, #431, #432 (no timer, by the owner's approval; not run on an iOS device,
where the hang was seen; reopen if it recurs after a re-pin). Open by the owner's decision: #428 (the owner watches the
MP4 and judges the free camera's feel) and #429 (the editor move to 6000.6.10f1, a full gate cycle the owner starts).
Wickling re-pins to a commit that carries #433. No device run was made in this session.

## Earlier continuation — #430 game-drawn consent flow

#430 (`GameConsent.Register`, `Consent: GameFlow`, `GameConsentAnalyticsConsent`) is `16b2d5cf` on `origin/main`; its
follow-ups #433 (CS0618 lookups, the child-build log, child-build analytics discard) are `4dc286ad` and `465fa448`.
Evidence, the Windows re-run and what is still not run (the screen on a device) are in `RELEASE_GATES.md`,
"Game-drawn consent flow". The optional parts are #431 (per-platform `Consent`) and #432 (fallback for a flow that never
answers; waits for the owner's policy decision). Wickling re-pins to the commit that carries #433.

## Earlier continuation — closure follow-up

**Framework issue state:** #100, #418 and #420 were closed with scoped evidence comments;
the final GitHub open-issue query returned an empty list. All eight existing consumer
migration issues received the new pin and follow-up instructions. Game release/performance
acceptance remains tracked in those game issues; it is not claimed complete here.

The owner asked to complete and close every open framework issue. New #420 was included.
Published code pin is now `74c36655816014ed30e38ea77d526db0c9549be0`:
reusable recurring ads timers (#418) and package-owned development build symbol (#420).
Evidence is canonical in `RELEASE_GATES.md`, “Closure follow-up”; raw artifacts under
`Nexus/artifacts/closure-100-418-420/`. Host651/0, EditMode1027/0/2, PlayMode326/0/2;
LevelPlay47/47 and AdMob45/45 on Android editor target. Device100 timer resets/cancels0alloc.

#100 additionally ran ChipCrisis's real save path in an isolated application: 48 calls,
median4.550ms/max6.306ms; Play-installed owner data was inaccessible to backup and left
untouched. This is a generated unlocked-zone/RamCrisis fixture, not the original save;
overall game frames still have spikes. Consumer acceptance details stay in ChipCrisis#20.
ChipCrisis has a #20 re-pin and isolated test tools from this session; inspect that repo's
status before editing. Wickling/Gigawatt had unrelated work and were not repinned here.

ChipCrisis follow-up is committed/pushed as `f045846`, with a clean tree: all eight
Nexus packages at74c36655; EditMode764/0/12. Its graphics PlayMode124/2 found two
factory-settings assertions expecting four buttons although the game includes a privacy
row. This game-specific mismatch is tracked in ChipCrisis#21, not a new framework issue.
ChipCrisis#20 stays open for full game/original-save acceptance. The initial headless UI
run was rejected for RenderTexture failures. No subagents were used in this follow-up.

Refresh GitHub before resuming; the older checkpoint below describes the earlier state.

## Resume point — 2026-11-10

The owner asked to fix consumer-reported framework issues individually with evidence,
notify affected games through issues, and preserve the resume point before restarting
the terminal. The owner subsequently stopped failing subagent calls. Those calls created
no sessions; do not assume any reviewer completed work or any subagent is running.

Read `../CLAUDE.md` for repository rules. Work directly on `main`; fixes belong in this
repository and games re-pin through their own migration issues. Preserve real game saves.

## Published work

- Common **consumer pin**: `0a2eb4274211ff9dcdea4ddea3a25eddbd81aa0d`.
- Evidence/handoff record already published at `87ada88ffe82183182c49fbf00ab531ea9c7d0e1`.
  This note's commit follows it and changes documentation only.
- `385391e9`: #417 inherited injection accessor preservation and release stripping probe.
- `6d3514bd`: #100 encrypted-write allocation reduction and regressions.
- `a430ad83`: #416 removes idle JNI polling; Intent commands use startup/focus gain.
- `0a2eb427`: #418 deadline scheduler and #415 facade/process-restart proof.
- Working tree was clean before this documentation update; remote main matched `87ada88f`.

## Live issue state at this checkpoint

Verified with GitHub CLI, not inferred from commit messages:

| Issue | State | Remaining work |
|---|---|---|
| [#417](https://github.com/CevatAYDN/GameContainer/issues/417) | Closed | Package fix proved red→green on a non-development Android IL2CPP High/OptimizeSize player. Game-specific production acceptance is in the migration issues. |
| [#416](https://github.com/CevatAYDN/GameContainer/issues/416) | Closed | Polling removed; Android development/release assembly checks passed. Games still verify cold-start/focus commands and idle captures. |
| [#415](https://github.com/CevatAYDN/GameContainer/issues/415) | Closed | Existing quarantine fix additionally proved with static Ads, real PlayerPrefs and three process launches. |
| [#100](https://github.com/CevatAYDN/GameContainer/issues/100) | **Open** | ChipCrisis must remeasure the original save/crowd missed-vsync scenario after re-pin. Allocation reduction is not proof the game hitch is gone. |
| [#418](https://github.com/CevatAYDN/GameContainer/issues/418) | **Open** | Wickling must rerun its perf gate. Coroutine/yield/wrapper allocations were removed, but one unique cancellation-token allocation per Schedule remains, plus possible caller closures/list growth. |

The required commit hook also exposed [#419](https://github.com/CevatAYDN/GameContainer/issues/419)
while writing this handoff: the GS5 host fixture subtracted two hours from machine
uptime shortly after reboot, yielding an invalid negative counter and then Earn(0).
The fixture now records the calculator's actual clock, models either a valid same-boot
absence or a previous boot, and caps the asserted absence at two hours. Runtime validation
is unchanged. Failed-hook evidence: `commit-session-handoff.log` in the local artifact folder.

**Do not report all consumer issues resolved.** Three framework issues are closed; two have
published improvements but incomplete performance acceptance. No newer open issue was
present at this checkpoint. Refresh this information before acting.

## Evidence and notifications

Canonical evidence, APK hashes, measurement limitations and all eight consumer issue links:
[RELEASE_GATES.md](RELEASE_GATES.md), section “Consumer hotfix verification”.
Local raw artifacts: `Nexus/artifacts/issues-417-100-416/` (git-ignored; not uploaded).

Last completed checks: host 651 passed / 0 failed; EditMode 1026 passed / 0 failed /
2 skipped; final PlayMode 325 passed / 0 failed / 2 skipped. These are existing recorded
runs, not tests rerun merely for this memory update.

Priority consumer follow-ups:
- [ChipCrisis#20](https://github.com/CevatAYDN/ChipCrisis/issues/20): #100 plain frame/vsync
  metrics and separate allocation callstacks on the same save/crowd workload.
- [Wickling#10](https://github.com/CevatAYDN/Wickling/issues/10): #418 perf gate and timer
  lifecycle acceptance; #416 idle polling disappearance.
- [Formatron#13](https://github.com/CevatAYDN/Formatron/issues/13): remove the temporary
  #417 accessor workaround after re-pin and verify the actual production AAB/start/restart.
- Gigawatt#7, Pixel-Flow-Clone#3, IdleProject#2, PuzzleGame#2 and RingFlow#7 were also
  created. Do not duplicate these notifications or change consumer pins silently.

## First steps in the next session

1. Run `git status --short --branch` and refresh `gh issue list --repo
   CevatAYDN/GameContainer --state open --limit 100`.
2. Read new comments on #100/#418 and the three priority consumer issues. Check their
   actual pin and artifacts before declaring acceptance. If a new framework issue has
   arrived, reproduce it, implement the smallest architecture-consistent fix, and verify.
3. If #100/#418 still reproduce, continue from the remaining attributed allocation/
   timing evidence. Do not close solely because an isolated benchmark improved.
4. Publish any new verified change on main and update the existing game issue with the
   full SHA and exact migration/verification steps.

## Execution details worth retaining

- Unity 6000.6.8f1; one editor per project. Check running processes before starting batch
  commands. Use PowerShell `Start-Process -Wait -PassThru` and check exit code/result XML.
- Another game session used the same phone during this work. Avoid overlapping device
  runs and do not stop another game's process. Capture logs for the current PID.
- The release injection probe deliberately parks/restores the generated binder; otherwise
  binder references can hide missing preservation. Do not leave it parked or reset user data.
- Unity's thread-allocation API returned zero even for the positive control. Valid storage
  measurements used positive-controlled whole-frame allocation upper bounds instead.
- #416 foreground debug commands require HOME then `am start`; force-stop loses held rewards.
- `check-versions.py --newest` was partial: seven GitHub HTTP 403 responses and a newer
  Pipeline prerelease. Offline consistency passed. No dependency upgrade was made here.
