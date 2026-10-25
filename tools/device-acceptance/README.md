# Device acceptance (Android)

Runs an Android player on the connected phone and records what a build gate cannot: the app
starts, survives a soak, comes back from the background, restarts after the OS kills it, and
leaves no crash, ANR or Unity exception in logcat. Evidence lands under
`Nexus/artifacts/<RunName>/` (`device-acceptance.json`, `logcat.log`; git-ignored). The
repository's `docs/RELEASE_GATES.md` cites the latest run.

```powershell
# 1. Build the development project's Android IL2CPP player (Assets/Scenes/NexusStarter.unity)
& "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath Nexus -executeMethod NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPP -logFile Nexus\artifacts\android-build.log
# 2. Run the acceptance on the connected device (the phone owner accepts the USB install)
./tools/device-acceptance/run.ps1 -RunName device-<date> -SoakSeconds 300
```

Any APK works (`-Apk path`): the package and launch activity are read from the archive with
`aapt`, so a game's own player goes through the same phases.

| Phase | What is checked | Fails when |
|---|---|---|
| Cold start | a process exists 15 s after launch | no process |
| Soak (`-SoakSeconds`, default 300) | the process stays alive; `dumpsys meminfo` TOTAL PSS sampled every `-SampleSeconds` | the process dies, or PSS grows more than `-MaxPssGrowthPercent` (default 50) between the first and last sample |
| Background and resume | HOME, 10 s, relaunch; the process is back (same pid recorded, a new pid is reported, not failed: Android may recreate it) | no process after relaunch |
| Kill and restart | `am force-stop`, relaunch; a process exists after 15 s | no process |
| Logcat | `FATAL EXCEPTION`, `ANR in <package>`, Unity exception lines, `[Nexus Ads] PolicyViolation` lines, counted only on lines logged by this app's processes (other apps on the phone log their own; a process Android recreated on resume counts as the app's); Unity's own Play Asset Delivery probe (`ClassNotFoundException ... AssetPackManager`, logged by every player without Play Core) is ignored | any of the four |
| Ads (`-ExpectAds Provider\|Simulator\|Idle`, off by default) | `com.nexus.ads`'s `[Nexus Ads] started mode=...` line on every launch (the package writes it from issue #52 on); the lines are recorded under `adsStarts` | fewer start lines than launches, or one with another mode |

`-ExpectAds` checks which side of the ad gate the phone is on: `Provider` where the real SDK
starts (a development build: a device in the settings' test device list with test mode on, or the
provider's published test units), `Simulator` on a device that is not listed (rule
`UnregisteredDevice`), `Idle` where nothing may run. The run
shows no ad. Whether the SDK then serves test ads is decided by the mediation's own dashboard;
LevelPlay adds no label of its own to a test ad (AdMob's "Test Ad" badge has no counterpart
there) and logs no line naming one; only the creative itself may say "test".

Limits: no storage-interruption test (filling or detaching storage is not done safely on a
phone), no gameplay assertions, one device model. Run it on a game's player as well; the
framework's own player only shows that the runtime boots and idles.
