# Nexus Ads

The ad package family's core: an engine-free ad core, a Unity layer, editor setup tools and a
validator, and a simulator provider. Mediation SDKs live in separate provider packages (`com.nexus.ads.levelplay`,
`com.nexus.ads.admob`; a MAX provider is not planned while LevelPlay is the only mediation, issue #86); Nexus games connect through `com.nexus.ads.bridge.nexus`
([#12](https://github.com/CevatAYDN/GameContainer/issues/12)). The plan, decisions and phases are in
[issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes are in [CHANGELOG.md](CHANGELOG.md). A game still on the
removed legacy `AdMobAdapter` moves here with [MIGRATION.md](MIGRATION.md).

## Install

In `Packages/manifest.json`, by git URL pinned to a commit:

```json
"com.nexus.ads": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#<commit>"
```

`<commit>` is the full 40-character commit hash. The Package Manager does not take a short one:
`#9e793d5` ends in `Could not clone ... Make sure [9e793d5] is a valid branch name, tag or full commit hash`.
The package brings `com.unity.ugui` with it. Every other family package goes in at the same commit (the
validator's `manifest.pins` check; **Nexus > Ads > Pin family packages to one commit** aligns
them), and **Prepare Android** adds the LevelPlay or AdMob provider package at that commit itself.
Next: the settings ([Using it without Nexus](#using-it-without-nexus)), then the mediation's README
([LevelPlay](../com.nexus.ads.levelplay/README.md), [AdMob](../com.nexus.ads.admob/README.md)).

## What is here

| Folder | Assembly | Content |
|---|---|---|
| `Runtime/Core` | `Nexus.Ads.Core` (`noEngineReferences: true`, no references) | Placements, `AdPolicy` floors, `PolicyGuard`, `AdController`, `RetryPolicy`, `ProviderGate`, `MediationLock`, `SimulatorProvider`, the ports in `Ports.cs`. Compiled and proven in the host harness (`dotnet run --project tools/nexus-benchmark`, suite `AdsCoreSuite`). |
| `Runtime/Unity` | `Nexus.Ads.Unity` (references Core and `UnityEngine.UI`) | `AdsSettings` asset, `ProviderStatusResolver`, `AdsBootstrap`, the static `Ads` facade, `SimulatorOverlay`, `AdRewardedButton`, the port implementations. |
| `Editor` | `Nexus.Ads.Editor` (references Core and Unity) | `AdsValidator` (+ `RunCli`, pre-build hook), Prepare Android (`AndroidSetup`, `PrepareCli`), `AdsProjectActions` (the settings asset, the `AdPlacements` generator, app-ads.txt, link.xml, manifest pinning, the SDK install and the Test-tab keys); `PlatformCatalog.json` (links, official test data with source, SDK distribution, setup steps EN + TR) read through `PlatformCatalog`. |
| `Editor/Nexus` | `Nexus.Ads.Editor.Nexus` (references the Editor assembly and `com.nexus.core.editor`; compiled only with `com.nexus.core` 0.6.0 or newer installed, `NEXUS_CORE`) | The Nexus window's Ads section: the Setup, Settings and Placement Tester tabs (UI Toolkit), and `AdsStatusSource`, the validator in the window's Status list. |
| `docs` | | [POLICY_COMPLIANCE.md](docs/POLICY_COMPLIANCE.md) ([Türkçe](docs/POLICY_COMPLIANCE_TR.md)): every platform rule and where the package covers it. |
| `Samples~/PlainUnity`, `Samples~/Nexus` | (imported by the Package Manager) | Ads without Nexus over the static facade; a Nexus game's lifecycle with `NexusAdsBridge.Connect`. |
| `Tests/Editor`, `Tests/Runtime` | `com.nexus.ads.editor.tests`, `com.nexus.ads.tests` | EditMode tests of the settings, the resolver, the validator (through a fake project probe) and the text generators; PlayMode tests of the bootstrap, overlay, scheduler and button. |

## Rules the core enforces

`PolicyGuard` answers every show with one of the rule ids in `PolicyRules`; a block raises a
`PolicyViolation` event and a `Violation` telemetry event. The editor and development builds log
every block as an error; a release build logs the first block of each rule and placement as a
warning (`[Nexus Ads] PolicyViolation <rule> (<format> '<placement>'): <reason>`). The guard cannot
be turned off; the floors in `AdPolicy` can only be tightened, by the settings, a game or remote
configuration alike. The kill switch and the ad-free entitlement are not violations: they answer
`NotReady` before the guard runs, and hide a banner on screen. The kill switch stops every format, and
turning it off, or removing the remote configuration, loads them again; the
ad-free entitlement removes interstitials and banners and keeps rewarded ads, which the player asks
for (#195). The policies behind each rule, with the pages they were read from, are in the register.

| Rule | Blocks |
|---|---|
| `UnknownPlacement` | a placement name not declared in the settings, or declared with another format |
| `InterstitialNaturalBreakOnly` | an interstitial on a placement without `NaturalBreak` |
| `InterstitialFrequency` | an interstitial inside the gap, in the first seconds of a session, before the player has had the policy's number of earlier sessions on the device, over a session or day cap, or right after a rewarded ad; a show of any format before its placement's first-show delay, inside its cooldown or over its session or day cap |
| `InterstitialAfterTouch` | an interstitial within the policy's wait after the player's last touch (off by default) |
| `NoStackedAds` | a second full-screen ad while one is open |
| `NoAdInBackground` | a show while the app is not visible |
| `NoRequestBeforeConsent` | a show before the consent flow finished |
| `SingleMediation` | a second mediation provider in the process |
| `TestModeRequired`, `UnregisteredDevice` | decided by `ProviderGate` before the SDK starts (see below) |
| `RewardedOnRequestOnly`, `ChildSettingsForwarded`, `NoAutomaticRequests`, `NoAdViewInterference` | structural: the API has no way to break them; the harness proves the shape |

Defaults of `AdPolicy.Default`: 30 s between interstitials, none in the first 60 s of a session,
none within 30 s after a rewarded ad, no session or day cap, a new session after 30 min in the
background, interstitials from the first session, no wait after a touch. Locked by `AdsCoreSuite`
AP1–AP6 and AP8–AP11.

`MinLifetimeSessionsBeforeInterstitial` (remote key `ads.min_lifetime_sessions_before_interstitial`,
#128) holds interstitials back until the player has had that many earlier sessions on the device: a
cold start and a return after the resume gap each count one. The count is kept in `IPacingStore`
next to the pacing rows, so a player who had the game before the update starts at 0 and clearing
the app's data starts over (`AdsCoreSuite` PG26–PG29, PW8).

`NoInterstitialAfterTouchSeconds` (remote key `ads.no_interstitial_after_touch_seconds`, #126) blocks
an interstitial as `InterstitialAfterTouch` for that many seconds after the player's last touch, so
one that opens under a finger in a game played by holding or dragging does not take the next tap.
The bootstrap adds `UnityTouchInput`, which records every frame a finger or the left mouse button is
down or comes up, read from the Input Manager or from Input System 1.20.0 or newer; a game whose
players act through other input (a gamepad, keys) calls `UnityTouchInput.Touched()` itself. A build
that reads no pointer logs one warning when the wait is set. A touch itself raises no
`ReadyChanged`; an `IsReady` or `Show` that meets the wait (`Show` answers `Blocked`) arms the pacing
wake, so `ReadyChanged` follows when the wait ends and a game that keeps its break open asks again
then. While the player keeps touching, a wake may raise `ReadyChanged` with nothing changed
(`AdsCoreSuite` PG30–PG33, PW9–PW11).

The gap between interstitials, the wait after a rewarded ad and a placement's cooldown run from
the show, across a resume and a restart: `SessionTracker` keeps each last show as a UTC wall-clock
time in `IPacingStore` (`PlayerPrefs` in the Unity layer), next to the per-day counters. A show
stored ahead of the wall clock (the device clock was set back) counts as just shown. The
first-seconds floors and the session caps start over with each session (`AdsCoreSuite` PG21–PG24).
The per-day counters follow the device's UTC day: a counter stored on a later day than the clock's
(the clock was set back) keeps counting; a clock set forward cannot be told from a real day change,
since the package reads no server time, and starts the day caps over (#195, PG25).

## Which build talks to which SDK

`ProviderGate.Evaluate(build, providerConfig, deviceAdvertisingId)`:

| Build | Configuration | Result |
|---|---|---|
| Editor | any | simulator |
| Release | test mode, test devices or sample units present | nothing runs (`TestModeRequired`, logged as an error): the validator stops the build first |
| Release | live keys | the SDK |
| Development | Google's sample units | the SDK on any device |
| Development | test mode on, device in the Test tab's list | the SDK with test ads |
| Development | test mode on, device not listed (or list empty) | simulator (`UnregisteredDevice`) |
| Development | test mode on, advertising id unavailable (missing, or all zeros when ad personalization is off) | simulator (`UnregisteredDevice`); such an id never counts as listed |
| Development | test mode off | nothing runs (`TestModeRequired`) |

So a development build handed to a teammate never requests a live ad.

The device's advertising id, for the Test tab's list and the mediation dashboard: before the first
build, Google's ads settings page on the phone shows it as "Your advertising ID"; the command
`adb shell am start -a com.google.android.gms.settings.ADS_PRIVACY` opens that page. A development
build's debug panel shows the same id with a copy button where the gate reads it, with a mediation
selected and test mode on; elsewhere nothing reads the id (issue #133).

`ProviderGate.Host(build, gate, mediationSelected, providerRegistered)` then decides what the bootstrap
runs. The simulator (a full-screen overlay labelled "Simulated Rewarded ad" or "Simulated Interstitial ad") runs only where the gate chose it,
or in the editor and development builds while no mediation is selected. A blocked gate, or a selected
provider that is not in the player (package not installed, assembly stripped) where the gate chose the
SDK, runs nothing: its provider is `NoAdsProvider`, so the controller never starts, whoever calls `Start` (the bridge's
`Initialize` included, issue #165), every show answers `NotReady`, the rewarded button
stays hidden and the reason is logged as an error. A release build never runs the simulator (issue #19; locked by
`AdsCoreSuite` GT10–GT13).

The simulator's overlay is IMGUI. A game that draws its own ad screen (UI Toolkit) turns the
bootstrap's **Simulator Overlay** off (`simulatorOverlay: false` to `Configure`): the bootstrap adds no
overlay, logs one warning, and every simulated show stays open until the game ends it. The game shows
its screen on `AdsBootstrap.Instance.Simulator.Opened` and calls `CompleteShow` with `Rewarded`, `Closed`
or `Failed`; it returns false when it did not end the show. `SimulatorProvider.MinShowSeconds`, set in
the bootstrap's `Started` event (`Simulator` is null outside Simulator mode), keeps a show from closing
for that many seconds, as an unskippable rewarded does: `CompleteShow(Closed)` is refused while
`CanClose` is false, the overlay and the Placement Tester grey their Close button, and a reward or a
failure ends the show at any time. **Debug Panel** off (`debugPanel: false`) leaves the development
build's debug panel out (issue #132; `AdsCoreSuite` SM11–SM14, PlayMode
`OverlayAndPanelOff_AddNoIMGUI_OnlyTheGamesCompleteShowEndsTheShow`).

## Using it without Nexus

1. Create the settings asset: **Assets > Create > Nexus > Ads Settings**, saved as
   `Assets/Resources/NexusAdsSettings.asset`. Declare the placements (name, format, `NaturalBreak`,
   cooldown, caps), the privacy flags and, on the Release tab, the live keys. The Test tab holds the
   provider's test data and the device list (the wizard of #11 fills it).
2. Put one `AdsBootstrap` on a GameObject in the first scene. The bootstrap marks that GameObject
   `DontDestroyOnLoad`, so the GameObject and everything on it survive scene loads. A second bootstrap
   (that scene loaded again, or one in another scene) removes only its own component, leaves its
   GameObject and the rest on it, and logs a warning naming both GameObjects (#195). In the editor, in
   a development build on a device that is not listed (`UnregisteredDevice`) and in a development
   build while no mediation is selected, the simulator runs with a full-screen overlay; where the gate
   chose the SDK, a selected provider that is not installed runs nothing. In every build it logs what it
   started, without ids: `[Nexus Ads] started mode=<Provider|Simulator|Idle> mediation=<kind>
   gate=<Provider|Simulator|Blocked> rule=<rule or -> testMode=<on|off> consent=<flow>(<from>)` (one line;
   `tools/device-acceptance/run.ps1 -ExpectAds` reads it). `consent` names the flow that runs before the
   mediation and where it came from (#154): `registered` (the flow the settings name: Google's UMP from
   `com.nexus.ads.admob`, or a flow the game passed to `AdsBootstrap.RegisterSharedConsentFlow`), `provider` (the provider's own:
   AdMob runs UMP; LevelPlay's is a pass-through that shows no form, which runs only with `Consent: None`),
   `simulator`, or `none` when no ads start. LevelPlay ships no consent form, so with LevelPlay selected
   the settings' **Consent before LevelPlay** (`AdsSettings.Consent`) names the flow that runs before it:
   `GoogleUmp` (the default; Google's form, which `com.nexus.ads.admob` registers), `GameFlow` (the game's
   own `IConsentFlow`, registered with `AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GameFlow, factory)`
   before `StartAds()`, which forwards the answer with `LevelPlayProvider.SetGdprConsent`) or `None` (no flow
   runs, Google's form included). Each source has its own registration and only the one the setting names
   runs, whatever the order the registrations came in (#181). When the flow it names is not registered at the
   start, no ads start and the bootstrap logs `[Nexus Ads] no ads run:` with what is missing; the Ads
   section's Settings and Setup tabs and the debug panel show the same decision (`BuildOutlook.Decide`, `BuildOutlook.MissingConsent`;
   #154, #181). A game that starts ads later (after its own
   explanation, or never for a player who owns its ad-free product) turns the bootstrap's
   `startOnAwake` off (or passes `startOnAwake: false` to `Configure` on a bootstrap made in code) and
   calls `AdsBootstrap.Instance.StartAds()` when it is ready; until then nothing reads the advertising id
   or touches the consent flow and the SDK, a show answers `NotReady`, and `NexusAdsBridge.Connect` and
   `Ads.OnReadyChanged` complete when it starts (issue #62). An `Ads.OnReadyChanged` subscription
   hears the bootstrap running when it was made and any that starts later, so one made before a
   bootstrap exists works too (#195).
3. Generate the `AdPlacements` constants (**Nexus > Ads > Generate Placements**, path on the
   settings) and show ads through the facade:

```csharp
using Nexus.Ads;
using Nexus.Ads.Unity;

if (Ads.IsReady(AdPlacements.ShopRefill))
    Ads.Show(AdPlacements.ShopRefill, result =>
    {
        if (result != AdResult.Rewarded) return;
        Wallet.Add(50);
        var paid = Ads.PendingReward;   // this show's receipt: paid, so acknowledged
        if (paid.HasValue) Ads.AcknowledgeReward(paid.Value.Id);
    });

Ads.Show(AdPlacements.LevelEnd); // interstitial: NaturalBreak placement, paced by the guard

// at start-up, before or after the ads start: a reward the previous run was killed in the middle of
var receipt = Ads.PendingReward;
if (receipt.HasValue) { Wallet.Add(50); Ads.AcknowledgeReward(receipt.Value.Id); }
```

Or drop `AdRewardedButton` on a uGUI Button with a `CanvasGroup`: it hides itself while nothing can
show and raises `OnRewarded` once per paid ad.

A UI that shows or hides an ad button follows `Ads.OnReadyChanged` and asks `Ads.IsReady` when it
fires, as `AdRewardedButton` does; it does not poll every frame. The event fires on a load, a load
failure, the end of a show, consent, visibility, the kill switch, the ad-free entitlement and the end
of a pacing window (a placement's cooldown or first-show delay, the interstitial gaps, a day cap at
the UTC day change, issue #95; the wait after a touch that `IsReady` or `Show` met, #126). A poll reaches the SDK each time the 0.1 s answer reuse runs out,
which on Android is a JNI call that allocates.

Full-screen ads pause `AudioListener` and set `Time.timeScale` to 0 until they close (switches on
the bootstrap). Ad events reach `AdsBootstrap.Instance.Telemetry.AddSink(...)`. With the bootstrap's
**Log Events** (on by default) the editor and development builds log every event; otherwise, and
always in a release player, each ad failure is logged as a `[Nexus Ads]` warning once per cause
(issue #164): a load that failed or found no fill (once per format and reason), a refused consent, a
failed start (once until the SDK starts, then one `Initialized` line), a consent flow or an SDK start
still unanswered after 30 s (`Unanswered`, once each; the wait goes on), every failed show (the AdMob
and LevelPlay providers write the SDK's reason on the line before), a subscriber or telemetry sink
that threw while a show was answered or readiness changed (`HandlerFailed`, once per step and
exception; issue #176), every turn of the kill switch on or off (`KillSwitch on` / `off`, issue #234;
a configuration that changes what runs), and in a release build the policy block above. Successes are
not logged.
`InitFailed`, `ConsentRefused`, `Unanswered`, `HandlerFailed` and `Initialized` are telemetry events
too. A show's result reaches the game exactly once whatever a subscriber, a sink or the reload
throws; an SDK load that throws is a failed load, retried with the back-off. A loaded ad the
provider no longer has (AdMob drops one after an hour, or at a consent change) is seen at the next
readiness check and reloads, with an `Expired` telemetry event (issue #175). A paid reward is written to the store before the game hears of
it (`PendingReward`, read from `PlayerPrefs` and so available before the ads start); `AdRewardedButton`
acknowledges it after its listeners ran, the bridge's `IAdService` once the game's callback returned,
and a game that saves asynchronously acknowledges it itself. One receipt is kept: a reward written
over an unacknowledged one is logged in every build (`RewardReplaced`, issue #178).

A show the provider never answers is ended as `Failed` after `AdController.ShowAnswerSeconds` (10 s)
with the app visible, with a `ShowFailed` line that names the placement (issue #180). The time counts
from the request until the ad opens, and again from the app's return when the ad took the focus, as
Android's ad activity does. An ad shown in the game's own window, such as the editor's simulator that
waits for a button, waits for the player. A reward the provider pays after the show was ended is kept
as the `PendingReward` for the game's next start.

## Editor tools

The Setup, Settings and Placement Tester tabs are the Nexus window's **Ads** section, in English and
Turkish with the window's language (`Editor/Nexus`, compiled only with `com.nexus.core`; #193). Without the
core, the settings asset is edited in the Inspector (**Assets > Create > Nexus > Ads Settings**), and the
menu items Generate Placements, Write link.xml and Pin family packages, `RunCli`, `PrepareCli` and the
pre-build hook remain.

- **Nexus > Ads > Settings**: the asset, the Test and Release tabs per platform (AdMob's Test tab
  is locked to Google's sample ids with a reset button; LevelPlay and MAX copy the Release keys), the
  device list, under the keys **Where these values come from** (the selected provider's setup steps and
  test-device location from `PlatformCatalog.json`, nothing written by the tab itself), **Prepare Android**
  for the tab being edited (the same steps and button as the Setup tab), placements, floors, privacy,
  app-ads.txt lines, and the validator buttons; a result shows the counts and each failing or warning
  check in its own box with its fix and check id (the Setup tab shows results the same way). Status badges
  come from `ProviderStatusResolver`. Above the keys, **What each build does** lists the editor, a
  development build (listed and other devices) and a release build in plain words, from `BuildOutlook`,
  the decision `AdsBootstrap` runs; the Setup tab shows the same lines and the debug panel shows
  `this build:`. A line is what the build decides to run; whether the mediation's server accepts the keys
  and fills an ad is known only from a run on a device.
- **Nexus > Ads > Setup**: from nothing to a live ad. Every provider and network row from the catalog with its
  status badge, next step, registration / docs / policy links, "Install SDK" (LevelPlay from the Unity
  registry, AdMob from OpenUPM with the scoped registry written for you, MAX through AppLovin's
  installer), the setup steps in English and Turkish, the project buttons (generate placements, write
  `Assets/NexusAds/link.xml`, pin the family packages to one commit), **Prepare Android**, the
  checklist of rules code cannot enforce, and the validator.
- **Prepare Android** (`AndroidSetup`, data in `Editor/PlatformCatalog.json`): the selected mediation's
  Android setup as a list of steps marked Done, Pending or Manual for the Test or Release tab, and one
  button that performs the Pending ones: the SDK and the Nexus provider package (at the commit
  `com.nexus.ads` is pinned to), the dependency resolver, the native SDK declaration (LevelPlay: the
  version LevelPlay's own installer takes, the newest one its `LevelPlayVersions.json` allows for the
  installed package, after the press has refreshed the project's copy of that file from LevelPlay's
  server as the installer does, issue #144), the network adapter the mediation needs to fill (LevelPlay:
  the Unity Ads adapter, the newest version in that file whose native SDK range holds the declared one), the
  custom main Gradle template with the catalog's Gradle lines, the custom Gradle properties template
  (LevelPlay: without it the External Dependency Manager reports Resolution Failed on every resolution
  and build),
  the resolution, and the AdMob App ID in the plugin's settings asset. A declaration of another
  version than the plan's is Pending and replaced, and the resolution is Done only when the template
  names the exact dependencies the declarations hold (issue #145). Adding packages reloads the
  editor, so that press stops there and the next press continues; every step is safe to repeat. MAX
  shows Manual rows until its provider exists. Command line:
  `Unity.exe -batchmode -nographics -projectPath <project> -buildTarget Android -executeMethod Nexus.Ads.Editor.AndroidSetup.PrepareCli -adsTab Test`
  without `-quit` (exit 0 all Done, 1 steps remain, 2 could not run, 3 packages added: run it again).
- **Nexus > Ads > Placement Tester**: in Play Mode, every placement with its readiness, a Show
  button, the result and the rule that blocked it, the simulator scenario and buttons, the pending
  reward, and the live event log.

## Validator

`AdsValidator.Validate(settings, tab, platform, probe, catalog)` is a pure function; the settings
page, the wizard, the CLI and the pre-build hook call it and own no rule of their own. The pre-build
hook (`AdsBuildGuard`) runs on Android and iOS builds only, validates the Test tab for a development
build and the Release tab otherwise, writes its report to `Library/NexusAds/ads-validation.json`, and
stops the build on a fail. With `com.nexus.core` installed, the Nexus window's Status tab lists the same
check for the active build target and the development flag, each warn and fail with its fix. An ad-free release (the Release
tab with Ad-free release on) starts no SDK, so the checks of a running mediation pass and say so
(`consent.form`, `provider.compiled`, `mediation.status`, `keys.release.missing`, `android.edm`, and
the Android plan checks `android.nativesdk`, `android.gradle`, `android.adapter`), while the checks
of what ships in the build still apply (#179). The development project's own asset is an ad-free
release for that reason. Batch mode:

```powershell
Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod Nexus.Ads.Editor.AdsValidator.RunCli -adsTab Release -adsPlatform Android -adsReport artifacts/ads-validation.json
```

Exit code 0 = no fail (warnings allowed), 1 = at least one fail, 2 = could not run (no settings asset,
bad arguments). `-adsTab` reads `Test` or `Release` in any case, here and in `AndroidSetup.PrepareCli`. The JSON report lists every check with `id`, `result` (`pass` / `warn` / `fail`),
`message` and `fix`, plus the counts and the provider status.

| Check id | Fails or warns when |
|---|---|
| `mediation.single` | fail: two mediation SDKs are in the project |
| `mediation.admob` | warn: the Google Mobile Ads SDK is in the project while LevelPlay or MAX is selected without `com.nexus.ads.admob` (only the selected mediation starts; the SDK still ships); with that package it passes, since Google's consent form then runs before the mediation |
| `consent.form` | LevelPlay is selected (it ships no form) and the settings' `Consent` names no flow that runs before it: `None`, or `GoogleUmp` without `com.google.ads.mobile` and `com.nexus.ads.admob`. Fail on the Release tab, which stops a release build; warn on the Test tab. `GameFlow` passes (the bootstrap starts no ads until that flow is registered). With `None` Google's form does not run even when its packages are in, and the fix names `GoogleUmp` (#181) |
| `mediation.selected` | fail: no mediation selected on a release that is not ad-free |
| `mediation.status` | fail: the selected provider is not Active for a release build (warn on the Test tab when NotInstalled: a development build runs the simulator on a device that is not listed and no ads on a listed one) |
| `keys.release.missing`, `keys.test.missing` | fail: a required field (derived from the placements) is empty; warn on the Test tab for LevelPlay/MAX (the SDK cannot start on a listed device) |
| `keys.release.testdata` | fail: Google's sample ids on the Release tab |
| `keys.test.official` | fail: AdMob's Test tab differs from the catalog's sample ids |
| `keys.test.mirror` | fail: LevelPlay/MAX Test tab differs from a filled Release tab |
| `devices.empty` | warn: no test device, every development build runs the simulator |
| `sdk.unitypackage` | warn: the SDK is a `.unitypackage` import; names the define to set |
| `android.admob.appid` | fail (AdMob selected): the Google Mobile Ads settings asset carries no App ID or one that disagrees with the tab's; a missing settings asset fails on the Release tab and warns on the Test tab. Fail (another mediation selected, Google Mobile Ads in the project): no App ID in that asset (the Android SDK stops the app at launch without one), or Google's sample App ID on the Release tab |
| `android.edm` | fail (release): External Dependency Manager missing |
| `android.nativesdk` | fail: the selected mediation's native SDK declaration is missing, names another version than Prepare Android's plan, or is not resolved at that version into the Gradle template (LevelPlay's Gradle build would stop at `IronSourceError` without it) |
| `android.gradle` | fail: the custom main Gradle template or one of the catalog's Gradle lines is missing (LevelPlay's native SDK 9.5.0 on Unity 6000.5: duplicate Kotlin classes) |
| `provider.compiled` | fail: the selected provider's Nexus package and its SDK are in the project but the provider is not compiled, because the SDK is below the provider asmdef's `versionDefines` expression; the message names the expression and the installed version (issue #143; before it the status read Active and no build ran ads) |
| `android.adapter` | fail: the network adapter the mediation needs to fill is not declared, or names another version than Prepare Android's plan (LevelPlay without the Unity Ads adapter builds, but every request ends in `509 Mediation No fill`) |
| `android.minsdk` | warn: minimum API below the provider's |
| `child.mediation` | fail: child-directed with a mediation whose policies forbid children's apps (MAX) |
| `child.networks` | warn: child-directed with an adapter of a network off Google's Families list (heuristic detection) |
| `placements.names` | fail: a name that is not a C# identifier, a duplicate, or an interstitial without NaturalBreak; warn: no placements |
| `policy.floors` | warn: a floor below the package default (raised at runtime) |
| `privacy.url` | warn: no privacy policy URL |
| `android.adid` | Android only. warn: custom manifest without the AD_ID permission on target 33+; fail: a child-directed app whose custom manifest does not remove the permission (`tools:node="remove"`), which the ad SDKs add on merge (Play Families, issue #194) |
| `android.targetapi` | Android only. fail (Release) / warn (Test): target API below Play's floor of 36 (`AdsValidator.PlayTargetApiFloor`); Automatic is read as the API level it builds with, the highest whole-numbered platform of the Android SDK in External Tools (issue #194) |
| `android.linkxml` | warn (release): no link.xml preserves `Nexus.Ads.Core` |
| `appads.lines` | warn: a detected network has no app-ads.txt line that starts with its ad system domain (`google.com` for AdMob, `unity.com` or `unity3d.com` for Unity Ads, ...) |
| `manifest.pins` | fail: the family's git-URL entries point at different commits |

## Using it with Nexus

Through `com.nexus.ads.bridge.nexus`: the bridge wraps the controller as the `IAdNetworkAdapter` of
`com.nexus.core`'s `AdService`, so game code written against `IAdService` does not change. Bind the
service and the factory, put an `AdsBootstrap` in the scene, call `NexusAdsBridge.Connect` once; the
[bridge README](../com.nexus.ads.bridge.nexus/README.md) and `Samples~/Nexus` show it.
