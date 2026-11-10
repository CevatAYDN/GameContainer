# Session handoff

## Current continuation — closure follow-up

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
