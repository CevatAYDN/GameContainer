# Nexus Ads LevelPlay Provider

Unity LevelPlay behind the `com.nexus.ads` core: `LevelPlayProvider` is the family's `IAdProvider`
over the Ads Mediation package (`com.unity.services.levelplay` 9.x, namespace
`Unity.Services.LevelPlay`). Nothing in a game references the SDK; the settings asset carries the App
Key and the ad unit ids, `AdsBootstrap` creates the provider when LevelPlay is the selected
mediation, and the simulator runs in the editor and on unlisted devices. Plan and phases:
[issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes in the family's
[CHANGELOG](../com.nexus.ads/CHANGELOG.md).

## Install

1. **Tools > Nexus Ads > Setup > Install SDK** on the LevelPlay row adds `com.unity.services.levelplay`
   from the Unity registry (or Package Manager > Ads Mediation). The package holds only the C# side:
   the native Android SDK comes from **Ads Mediation > Network Manager**, whose LevelPlay SDK row
   writes `Assets/LevelPlay/Editor/IronSourceSDKDependencies.xml` (`com.unity3d.ads-mediation:mediation-sdk`
   for 9.5.x), and from a dependency resolver (the Mobile Dependency Resolver LevelPlay offers on first
   load, or `com.google.external-dependency-manager`): **Assets > External Dependency Manager > Android
   Resolver > Resolve**. The same window installs the network adapters. Skipped, the Android build fails
   in Gradle with `cannot find symbol IronSourceError`.
   With Unity 6000.5, mediation-sdk 9.5.0 brings `kotlin-stdlib-jdk7` / `-jdk8` 1.6.0 next to Unity's
   `kotlin-stdlib` 1.8.22 and Gradle stops on duplicate classes; align them in
   `Assets/Plugins/Android/mainTemplate.gradle` (Player Settings > Publishing Settings > Custom Main
   Gradle Template), outside the resolver's markers:
   `implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')`. Both steps were needed for the
   device run of 2026-10-24.
2. This package, pinned to the same commit as `com.nexus.ads` (the validator's `manifest.pins`
   check): `"com.nexus.ads.levelplay": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.levelplay#<commit>"`.
3. **Project Settings > Nexus Ads**: select LevelPlay, enter the App Key and the ad unit ids on the
   Release tab, press **Copy from Release** on the Test tab, add your phone's advertising id to the
   device list.

The assembly compiles only while the LevelPlay package is installed (`versionDefines` →
`NEXUS_LEVELPLAY`); without it the package is inert.

## What the provider does

| Core call | LevelPlay |
|---|---|
| `Initialize(config)` | `LevelPlayPrivacySettings.SetCOPPA` from the child-directed / under-age flags, then with test mode `SetMetaData("is_test_suite", "enable")` and `SetAdaptersDebug(true)`, then `LevelPlay.Init(appKey)`; on `OnInitSuccess` one `LevelPlayRewardedAd` and one `LevelPlayInterstitialAd` per configured ad unit |
| `Load(format)` | `LoadAd()`; `OnAdLoaded` → `Loaded`, `OnAdLoadFailed` → `LoadFailed` with the SDK's code and message |
| `IsReady(format)` | `IsAdReady()` |
| `Show(format, placement)` | `ShowAd(placement)`; `OnAdDisplayed` → `Opened`; `OnAdClosed` answers `Closed` after a grace period of 1.5 s unless `OnAdRewarded` arrived (the SDK documents that the reward can fire after the close); `OnAdDisplayFailed` → `Failed` |
| `ShowBanner`, `HideBanner` | one adaptive `LevelPlayBannerAd` at the top or bottom, `ShowAd` / `HideAd` |
| `OpenTestSuite` | `LevelPlay.LaunchTestSuite()` (needs the test suite metadata, which test mode sets) |
| `Revenue` | each ad's `OnAdImpressionDataReady` → `AdRevenue(network, placement, format, revenue, "USD", precision)` |

Consent: LevelPlay ships no consent form. GDPR consent collected by Google UMP or a compatible CMP is
shared with the SDK automatically (SDK 7.7.0+); a game with another CMP forwards its answer with
`LevelPlayProvider.SetGdprConsent(bool)`, and a CCPA opt-out with `SetDoNotSell(true)`. The
provider's `IConsentFlow` is therefore a pass-through: nothing waits on it.

Test ads: LevelPlay publishes no test keys. The Test tab mirrors the Release keys; a development
build requests live SDK ads only on a device in the Test tab's list and that device also has to be
listed in the LevelPlay dashboard (Settings > Test devices) to receive test ads from a chosen
network. The debug panel on the device shows the advertising id to list.

## Verification

Compiled against LevelPlay 9.5.1 in the disposable project `Nexus/artifacts/levelplay-20261024/`
(see `docs/RELEASE_GATES.md`), with the Android build target: the package's editor platform exists only
when the active build target is Android or iOS; on another target `LevelPlay.Init` does nothing. EditMode tests lock the behaviour before initialization, the
pass-through consent flow, the bootstrap registration and the editor SDK's synchronous init and
loads; the PlayMode test shows a mock rewarded ad through the package's own editor platform and
proves a reward that arrives after the close pays once.
