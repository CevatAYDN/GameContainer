# Nexus Ads LevelPlay Provider

Unity LevelPlay behind the `com.nexus.ads` core: `LevelPlayProvider` is the family's `IAdProvider`
over the Ads Mediation package (`com.unity.services.levelplay` 9.x, namespace
`Unity.Services.LevelPlay`). Nothing in a game references the SDK; the settings asset carries the App
Key and the ad unit ids, `AdsBootstrap` creates the provider when LevelPlay is the selected
mediation, and the simulator runs in the editor and on unlisted devices. Plan and phases:
[issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes in the family's
[CHANGELOG](../com.nexus.ads/CHANGELOG.md).

## Install

1. `com.nexus.ads` by git URL at a full 40-character commit hash ([its Install section](../com.nexus.ads/README.md#install)).
2. **Project Settings > Nexus Ads** (or **Tools > Nexus Ads > Setup > Create the settings asset**): select
   LevelPlay, enter the App Key and the ad unit ids on the Release tab, press **Copy from Release** on
   the Test tab, add your phone's advertising id to the device list
   ([where to read it](../com.nexus.ads/README.md#which-build-talks-to-which-sdk)). Prepare Android reads
   the selected mediation from this asset; without it `PrepareCli` exits 2 (`no settings asset`).
3. **Tools > Nexus Ads > Setup > Prepare Android**, twice. The first press adds the Ads Mediation package
   (`com.unity.services.levelplay`, which holds only the C# side) from the Unity registry, this package
   at the commit of `com.nexus.ads` and the dependency resolver (`com.google.external-dependency-manager`
   from OpenUPM; `com.unity.external-dependency-manager` from the Unity registry when the project holds a
   disabled `com.google.external-dependency-manager` stub, as Firebase setups do, issue #61) in one Package
   Manager request; the editor reloads (`PrepareCli` exits 3: run it
   again). The second press does the rest of the Android setup; the validator (`android.nativesdk`,
   `android.gradle`, `android.adapter`) stops an Android build that misses any of it except the Gradle
   properties template:
   - the native SDK declaration `Assets/LevelPlay/Editor/IronSourceSDKDependencies.xml`
     (`com.unity3d.ads-mediation:mediation-sdk` 9.5.0 for package 9.5.1), the file LevelPlay's Network
     Manager installs, for the version the installed package names in its `LevelPlayVersions.json`;
     without it Gradle fails with `cannot find symbol IronSourceError`;
   - the Unity Ads adapter declaration `Assets/LevelPlay/Editor/ISUnityAdsAdapterDependencies.xml`
     (`unityads-adapter` 5.11.0 for native SDK 9.5.0; in the run recorded in `docs/RELEASE_GATES.md`
     the resolver brought `unity-ads` 4.19.0 with it), the newest
     `adapters.UnityAds` version in the same file whose `ironSourceSdkVersion` range holds the native SDK;
     LevelPlay's Network Manager shows it as a non-removable part of the SDK. Without it the build
     succeeds and every request ends in `509 Mediation No fill` (device run 2026-10-24, issue #16);
   - the custom main Gradle template with `implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')`:
     on Unity 6000.5 mediation-sdk 9.5.0 brings `kotlin-stdlib-jdk7` / `-jdk8` 1.6.0 next to Unity's
     `kotlin-stdlib` 1.8.22 and Gradle stops on duplicate classes;
   - the custom Gradle properties template `Assets/Plugins/Android/gradleTemplate.properties`, copied
     from Unity's: with a custom main template the External Dependency Manager writes
     `android.useAndroidX` / `android.enableJetifier` into it and, when it is missing, reports
     `Resolution Failed` on every resolution and build (issue #49); no validator check covers it, that
     message is the only sign;
   - the resolution into that template (Done when the template names `mediation-sdk` and `unityads-adapter`; a resolver set to
     write `.aar` files into `Assets/Plugins/Android` instead is not recognised yet).

   Other network adapters come from **Ads Mediation > Network Manager**. Proven from a clean project on
   2026-10-24: two presses, an Android build, the SDK reaching LevelPlay's server on a phone; with the
   owner's App Key and the Unity Ads adapter, a rewarded ad paid its reward and an interstitial closed on
   the phone (`docs/RELEASE_GATES.md`). Added by hand instead, this package goes in at the same full
   commit as `com.nexus.ads` (the validator's `manifest.pins` check):
   `"com.nexus.ads.levelplay": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.levelplay#<commit>"`.

The assembly compiles only while the LevelPlay package is installed (`versionDefines` →
`NEXUS_LEVELPLAY`); without it the package is inert.

The Ads Mediation package adds the scripting define `LEVELPLAY_DEPENDENCIES_INSTALLED` to the Player
Settings of the selected build target whenever the editor opens outside batch mode without it
(`ServicesCoreDependencyInstaller` in 9.5.1, after it checks for `com.unity.services.core`). The define
compiles only LevelPlay's editor code, the **Ads Mediation** menu with its Network Manager included; no
runtime file and none of its build processors read it, so the player is the same with or without it.
Commit it in `ProjectSettings/ProjectSettings.asset` for every build target the editor is opened on:
reverted, it comes back on the next open.

## What the provider does

| Core call | LevelPlay |
|---|---|
| `Initialize(config)` | `LevelPlayPrivacySettings.SetCOPPA` from the child-directed / under-age flags, then with test mode `SetMetaData("is_test_suite", "enable")` and `SetAdaptersDebug(true)`, then `LevelPlay.Init(appKey)` once `Application.internetReachability` reports a network (asked every 2 s, logged once as `[Nexus Ads] LevelPlay init waits for a network`; offline the SDK answers Init from its cached response and every load of the session then ends in `509 Mediation No fill`, issue #125); on `OnInitSuccess` one `LevelPlayRewardedAd` and one `LevelPlayInterstitialAd` per configured ad unit |
| `Load(format)` | `LoadAd()`; `OnAdLoaded` → `Loaded`, `OnAdLoadFailed` → `LoadFailed` with the SDK's code and message |
| `IsReady(format)` | `IsAdReady()` |
| `Show(format, placement)` | `ShowAd(placement)`; `OnAdDisplayed` → `Opened`; a rewarded ad's `OnAdClosed` answers `Closed` after a grace period of 1.5 s unless `OnAdRewarded` arrived (the SDK documents that the reward can fire after the close), an interstitial's answers `Closed` at once; `OnAdDisplayFailed` → `Failed` |
| `ShowBanner`, `HideBanner` | one adaptive `LevelPlayBannerAd` at the top or bottom, `ShowAd` / `HideAd` |
| `OpenTestSuite` | `LevelPlay.LaunchTestSuite()` (needs the test suite metadata, which test mode sets) |
| `Revenue` | each ad's `OnAdImpressionDataReady` → `AdRevenue(network, placement, format, revenue, "USD", precision)`, the format taken from the ad object (the SDK's `AdFormat` string is `rewarded_video` on Android) |

Consent: LevelPlay ships no consent form, and GDPR consent collected by Google UMP or a compatible CMP is
shared with the SDK automatically (SDK 7.7.0+). With `com.google.ads.mobile` and `com.nexus.ads.admob` in
the project, the bootstrap runs Google's consent form (`UmpConsentFlow`) before LevelPlay initializes, and
`PrivacyOptionsRequired` / `ShowPrivacyOptions` come from it (issue #63). The form shown is the message
published for the Google Mobile Ads App ID (Assets > Google Mobile Ads > Settings) under AdMob's Privacy &
messaging; the Android SDK stops the app at launch without that App ID, so the validator fails a build
without one (`android.admob.appid`) and warns when no consent platform is in the project (`consent.form`).
A game with another CMP turns the bootstrap's `startOnAwake` off, forwards its answer with
`LevelPlayProvider.SetGdprConsent(bool)` (a CCPA opt-out with `SetDoNotSell(true)`) and then calls
`AdsBootstrap.StartAds()`; the provider's own `IConsentFlow` is a pass-through.

Test ads: LevelPlay publishes no test keys. The Test tab mirrors the Release keys; a development
build requests live SDK ads only on a device in the Test tab's list and that device also has to be
listed in the LevelPlay dashboard (Settings > Test devices) to receive test ads from a chosen
network. The phone's advertising id: [Which build talks to which SDK](../com.nexus.ads/README.md#which-build-talks-to-which-sdk).

## Verification

Compiled against LevelPlay 9.5.1 in the disposable project `Nexus/artifacts/levelplay-20261024/`
(see `docs/RELEASE_GATES.md`), with the Android build target: the package's editor platform exists only
when the active build target is Android or iOS; on another target `LevelPlay.Init` does nothing.
EditMode tests (`Tests/Editor/LevelPlayProviderTests.cs`) lock the behaviour before initialization and
the pass-through consent flow. PlayMode tests (`Tests/Runtime/LevelPlayProviderPlayModeTests.cs`) run
over the package's own editor platform and lock the bootstrap registration, the editor SDK's
synchronous init and loads, a reward that arrives after the close paying once, an interstitial close
answering `Closed` at once, revenue carrying the format of the ad that earned it, and Init waiting
until a network is reachable.
