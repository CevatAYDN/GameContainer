# Migrating a game from `AdMobAdapter` to the ad family

For a Nexus game whose ads run on `com.nexus.ads.admob`'s `AdMobAdapter` behind `AdService`
(Gigawatt, Wickling and ChipCrisis pinned it at `c7a31eb`). Each owner migrated the game in its own
repository (decision 6 of [issue #8](https://github.com/CevatAYDN/GameContainer/issues/8)). Once the
three had moved, `AdMobAdapter`, `AdMobConfig` and their assembly were removed from
`com.nexus.ads.admob` ([issue #14](https://github.com/CevatAYDN/GameContainer/issues/14)): a game still
on the adapter migrates with this page on its current pin and re-pins afterwards, and the first
generated binder after the re-pin is written with `-ignorecompilererrors` (the old binder names the
removed type; core TROUBLESHOOTING, "Stale Generated Binder"). A step that does not work is an
issue in this repository, not a local edit of a package.

Game code written against `IAdService` (`RequestReward`, `ShowInterstitial`, `ReadyChanged`,
`PrivacyOptionsRequired`, `ShowPrivacyOptions`, `OnImpressionRecorded`) stays as it is: the bridge
puts the family's controller behind the same service.

## Steps

1. **Packages.** In `Packages/manifest.json`, every family package at one commit of this repository:
   `com.nexus.core`, `com.nexus.ads`, `com.nexus.ads.bridge.nexus`, and the provider of the selected
   mediation (the validator's `manifest.pins` check fails when the `com.nexus.ads*` entries differ, and
   **Tools > Nexus Ads > Pin family packages to one commit** rewrites those; neither covers
   `com.nexus.core`, which is re-pinned to the same commit by hand):
   - LevelPlay: `com.nexus.ads.levelplay` plus `com.unity.services.levelplay` 9.x; to keep Google's consent
     form (the form the legacy `AdMobAdapter` showed), also keep `com.google.ads.mobile` and add
     `com.nexus.ads.admob`: the bootstrap then runs Google UMP before LevelPlay, and the game's AdMob App
     ID stays in Assets > Google Mobile Ads > Settings (issue #63);
   - AdMob: `com.nexus.ads.admob` (its `Nexus.Ads.AdMob` assembly is the family provider) plus
     `com.google.ads.mobile` 11.6.0 or newer from OpenUPM (it brings Unity's External Dependency
     Manager as its own dependency).

   UPM does not install a git package's dependencies, so each one is listed by hand.
2. **Settings.** Open **Project Settings > Nexus Ads**: select the mediation, declare every placement
   name the game passes to `RequestReward` / `ShowInterstitial` (interstitial placements tick
   **Natural Break**; an undeclared name is blocked with `UnknownPlacement`), fill the Release tab with the
   live ids, and the Test tab (AdMob: Google's sample ids, filled by the page; LevelPlay: a copy of
   the Release tab plus the test device list).
3. **Android and scene.** Press **Tools > Nexus Ads > Setup > Prepare Android** until every step is
   Done (the resolver, LevelPlay's native SDK and Gradle lines, the AdMob App ID; see the
   [README](README.md#editor-tools)), and put an `AdsBootstrap` component in the first scene. A game
   that starts ads late (after its own explanation, or never for a player who owns its ad-free product)
   turns the bootstrap's `startOnAwake` off and calls `AdsBootstrap.Instance.StartAds()` when it is
   ready (issue #62).
4. **Wiring.** Replace the adapter registration with the bridge:

   ```csharp
   // before
   AdMobAdapter.Register(adFactory, new AdMobConfig { RewardedAdUnitIdAndroid = "...", /* ... */ });
   adService.SetNetworkAdapter(adFactory.CreateAdapter(AdMobAdapter.ProviderName));
   adService.SetInterstitialCooldown(45f);

   // after (store and product optional: owning "remove_ads" stops interstitials, rewarded ads stay)
   NexusAdsBridge.Connect(adService, adFactory, iapService, "remove_ads");
   ```

   `Connect` sets the service's interstitial cooldown to the policy floor; a game that wants a longer
   gap raises **Min Interstitial Gap Seconds** in the settings instead of calling `SetInterstitialCooldown`.
5. **Delete what the family now does.**

   | In the game | In the family |
   |---|---|
   | `AdMobConfig` ad unit ids, test ids next to release ids | Test and Release tabs; a development build reads Test, a release build Release |
   | Mock in the editor, real adapter on devices | `AdsBootstrap`: the editor runs the simulator, a development build the SDK with test data (AdMob: Google's sample units on every device; LevelPlay and MAX: only on a device listed on the Test tab, the simulator elsewhere), a release build the SDK with live ids |
   | `AdMobConfig.DebugGeographyEea`, `TestDeviceHashedIds` | `AdMobProvider.SetTestDevices(googleIds, eeaConsentGeography)` (AdMob; applied with Google's sample units only) or `UmpConsentFlow.SetDebugDevices(hashedIds, eeaGeography)` (Google's form before LevelPlay; applied in test mode only), before the bootstrap starts |
   | Interstitial cooldowns and per-day caps kept in `PlayerPrefs` | Policy floors and per-placement caps in the settings (`PolicyGuard`, per-day counters in `IPacingStore`) |
   | Muting audio or pausing time while an ad shows | `AdsBootstrap` pauses audio and time between open and close |
   | Load retries | `AdController`'s back-off, paused while the app is hidden |
   | A build script or code gate that refuses a release with test ids | `AdsValidator` pre-build hook on Android and iOS builds |

6. **Check.** Run **Tools > Nexus Ads > Setup** and the validator on both tabs (or
   `AdsValidator.RunCli`, see the [README](README.md#validator)); then a development build on a
   listed device and the device acceptance run.
7. **Record.** The re-pin commit and its test numbers go into gate 11 of
   [`docs/RELEASE_GATES.md`](../../../docs/RELEASE_GATES.md) through an issue in this repository.

## Per-game map

From the Phase 0 analysis in issue #8. All three games have moved since (gate 11 of
[`docs/RELEASE_GATES.md`](../../../docs/RELEASE_GATES.md)), each open for the owner's keys and store
settings (Gigawatt#1, Wickling#1, ChipCrisis#1); the table keeps what each one changed.

| Game | Before the migration | After the migration |
|---|---|---|
| Gigawatt | `MonetizationAdapters` picks the mock or `AdMobAdapter`; `InterstitialRules` keeps a per-day cap in `PlayerPrefs`; `MonetizationConfig` loads test or release ids from Resources; a `RealAdsForTest` switch | The bridge; the per-day cap and the cooldown become the interstitial placement's **Day Cap** and the policy floors; the ids move to the tabs (the Release tab stays out of public repositories); `RealAdsForTest` becomes the Test tab's device list. The delivered-customers condition is game state and stays in the game |
| Wickling | `MonetizationAdapters` picks the mock or `AdMobAdapter`; a `wickling-ads.txt` request file turns on real test ads; `AudioListener.pause` mute; a 10 s watchdog ends an ad that never answers | The bridge; the request file becomes the Test tab's device list; the mute is deleted (the bootstrap pauses audio). The watchdog stays in the game: the core never times out a show, because a rewarded video runs as long as it runs, and ending the game's waiting state is game UI |
| ChipCrisis | `AdNetworkService` (mock in the editor, AdMob on every device build); `AdsConfig` with Google's sample units and a `ReleaseProblem` gate that includes the banner; a build script that passes the development flag | The bridge; the Test tab holds the sample units (AdMob) or the copied Release keys (LevelPlay); no banner placement, so no banner id is required; the validator's pre-build hook replaces `ReleaseProblem`; the build script keeps the development flag, which selects the Test tab |
