# Provider reward fault testing (#412)

## Approved scope

Only `UNITY_EDITOR || DEVELOPMENT_BUILD` includes the reward fault API, provider
interception, panel controls and Android intent reader. No SDK source is changed.
The provider captures a **real received reward**, preserving its original show;
the hook never manufactures an earned reward. It can delay that delivery until
after close, replay it, or deliver A's reward when B actually opens. Provider
disposal cancels pending callbacks and releases their references. Retention is
bounded to one pending delivery and the last received reward.

Both LevelPlay and AdMob route the injected delivery through their existing
reward handler. The normal handler still deduplicates and the controller retains
late earned rewards as pending receipts. A shared hook avoids two subtly
different timing implementations. Debug output identifies the show, placement,
capture/delivery times and whether its native handle has already been released.

This verifies managed provider identity and late-reward recovery using received
SDK events. It does **not** prove that an SDK emits a new native event after its
object has been destroyed. An interrupted app cannot recover a reward whose
event was never received/persisted. For #384 closure the owner approved the
received-event contract: active ownership, successful durable receipt write,
idempotent confirmed game grant and id-specific acknowledgement. It excludes
unreceived native events, storage failure, and process death before persistence;
there is no server-side verification guarantee. This limitation remains documented
after issue closure.

Verification: deterministic hook tests, real pinned SDK-wrapper tests for both
providers, native audio GC regression (#411), engine compile checks and a release
assembly check excluding all hook types and provider entry points. Consumer
device execution remains in Formatron#10 and ChipCrisis#16.

## Use on a development player

Open the existing Ads debug panel (three fingers/F9, or the game's debug menu).
Arm **Delay reward 3s after close** before showing A, then watch a real test ad to
earn its reward. Close A; LevelPlay's 1.5s grace expires before delivery. The
`[Nexus Ads Fault]` log should show `captured-real-reward`, `closed`, and `deliver`
for the same show ID, with `released=True` on the delayed delivery. Check the
pending receipt's original placement and the game's idempotent replay.

For **Deliver A's reward when B opens**, arm before A. Earn and close A, then open
B. A's earned notification is delivered when the provider receives B's actual
opened callback. B must remain open and must not receive A's completion. **Replay
last received reward** must not recreate an acknowledged receipt. **Reset** ends
the experiment and delivers a held, received reward rather than dropping it.
Only one fault can be pending. Destroying the provider cancels the experiment.

### #413: continue the `next` experiment through B's own reward

Checking A immediately when B opens is insufficient. Let B earn and finish too,
and acknowledge **B's `DeliveringReward` id** in its live callback. Before #413,
B overwrote A; the old #412 tests stopped before this second reward and missed
the loss. With the queue fix A must remain the oldest `PendingReward` after B's
successful acknowledgement. Restart, grant A idempotently and acknowledge A;
restart again and confirm neither receipt is replayed. Repeat with the same and
different placements, retaining the original offer/context per receipt.

If B's grant commits but acknowledgement is interrupted, startup may process A
before retrying B. The game must deduplicate **all relevant ids**, not only the
last applied id; use `EarnOnce` for the economy or an atomic applied-id ledger.
Drain the pending queue until empty, stopping on any failure. A reward already
overwritten by the pre-#413 package cannot be reconstructed by this migration.

### #414: use different offer tokens even for the same placement

Persist `offer-A` (630) and `offer-B` (800) before their respective shows and pass
the keys through `Show(placement, context, callback)` or the bridge's contextual
`RequestReward`. Delay A using `next`, then let B earn/ack. A's pending receipt must
still contain `Context == "offer-A"`; B's delivery must contain `"offer-B"`. On
restart, resolve A's saved offer by that token and pay 630, not the newest 800.
Expected total is 1430 with receipt-id deduplication. Do not overwrite or delete
A's saved offer on `Closed`; a late earned event may still use it.

The Android development player also accepts an Intent string extra, independent
of whether the panel is visible. Resolve the real launch activity; do not assume
UnityPlayerActivity versus UnityPlayerGameActivity:

The package's `AdsFaultBuildSymbols` hook sets `NEXUS_ADS_FAULT_TESTS` from the
actual `BuildOptions.Development` flag and removes it for release (#420). This
works for normal player builds without editing a game's define list. Custom
`PlayerBuildInterface.CompilePlayerScripts` tooling does not run player build
callbacks: pass that symbol explicitly only for a development compilation.
Do not force the symbol through release `extraScriptingDefines`.

```powershell
$package = 'your.game.package'
$activity = (adb shell cmd package resolve-activity --brief $package | Select-Object -Last 1).Trim()
adb shell input keyevent KEYCODE_HOME
adb shell am start -n $activity --es nexus.ads.rewardFault delay:3
# Arm before A; deliver A during B:
adb shell input keyevent KEYCODE_HOME
adb shell am start -n $activity --es nexus.ads.rewardFault next
adb shell input keyevent KEYCODE_HOME
adb shell am start -n $activity --es nexus.ads.rewardFault replay
adb shell input keyevent KEYCODE_HOME
adb shell am start -n $activity --es nexus.ads.rewardFault reset
adb logcat -d -v threadtime -s Unity
```

The extra is removed when consumed; each new command needs a new intent. The
commands are read at startup and focus gain (#416), with no idle JNI polling.
For an already-foreground player, send HOME before `am start` as above; do not
force-stop it, because a held SDK reward lives in that process.
Accepted delay range: 0–120 seconds. Commands never start an ad or change consent. Test
devices, provider gate and test units remain the application's normal setup.
`NoFill` means no captured reward and is not a successful late-reward test.
