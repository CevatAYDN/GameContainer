# Nexus AdMob (`com.nexus.ads.admob`)

> **Who this is for:** level 3, shipping a game with Google AdMob.

Two assemblies, both inert until the Google Mobile Ads Unity plugin is installed:

| Assembly | What | Status |
|---|---|---|
| `Nexus.Ads.AdMob` (`Runtime/Provider`) | `AdMobProvider`, the ad family's `IAdProvider` over `com.nexus.ads` | Current; changes in the family's [CHANGELOG](../com.nexus.ads/CHANGELOG.md) |
| `com.nexus.ads.admob` (`Runtime`) | `AdMobAdapter`, an `IAdNetworkAdapter` for `com.nexus.core`'s `AdService` | Kept compiling until Gigawatt, Wickling and ChipCrisis have re-pinned to the family, then retired; changes in the core's [CHANGELOG](../com.nexus.core/CHANGELOG.md). Moving a game: [MIGRATION.md](../com.nexus.ads/MIGRATION.md) |

## AdMob provider for the ad family

`Nexus.Ads.AdMob` compiles only while both `com.google.ads.mobile` 11.3.0 or newer and `com.nexus.ads`
are installed (assembly definition `versionDefines` `NEXUS_ADMOB` and `NEXUS_ADS`, both in its
`defineConstraints`), so a game that pins this package for the old adapter alone is not affected.
Install the plugin from OpenUPM (scoped registry below; 11.6.0 brings Unity's External Dependency
Manager, `com.unity.external-dependency-manager`, as its own dependency and resolves its Android
libraries during the build), list
`com.nexus.ads` and this package at the same commit, select **AdMob** in **Project Settings > Nexus
Ads**, press **Tools > Nexus Ads > Setup > Prepare Android** (it writes the tab's App ID into the
plugin's settings asset) and put an `AdsBootstrap` in the first scene; the provider registers itself
before the first scene loads. A Nexus game adds `com.nexus.ads.bridge.nexus` and calls `NexusAdsBridge.Connect`.

What it does:
- **Consent:** asks the User Messaging Platform for an update on every launch (Google's privacy page
  requires it) and shows the form where the law needs it; consent kept from an earlier session lets ads
  load while this session's update runs. The privacy options form is `ShowPrivacyOptions`; a withdrawal
  destroys the loaded ads and further loads report "consent does not allow ad requests".
- **Request configuration** before `MobileAds.Initialize`: `AgeRestrictedTreatment.Child` when the
  settings mark the app child-directed or its users under the age of consent (Google's replacement for
  the deprecated `TagForChildDirectedTreatment` and `TagForUnderAgeOfConsent`, plugin 11.3.0), and the
  content rating. The App ID comes from the plugin's own settings asset (**Assets > Google Mobile Ads >
  Settings**); the validator's `android.admob.appid` check keeps it equal to the tab.
- **Ads:** one rewarded and one interstitial ad from the tab's units, an anchored adaptive banner;
  `OnAdPaid` reaches the family as `AdRevenue` (micros / 1,000,000, the network from the response
  info); the ad inspector is the test suite. Every SDK callback continues on the main thread.
- **Test ads:** Google has no test-mode flag. A development build reaches the SDK only with Google's
  sample units on the Test tab (the page fills them; otherwise the gate blocks and the build shows no ads), so every
  device gets test ads. To test the consent form outside the EEA, register the device with Google's
  own id before the bootstrap starts (the id the SDK prints in logcat as `setTestDeviceIds`, or in the
  Xcode console as `testDeviceIdentifiers`; on iOS add the id UMP prints as well):

  ```csharp
  #if DEVELOPMENT_BUILD
  Nexus.Ads.AdMob.AdMobProvider.SetTestDevices(new[] { "33BE2250B43518CCDA7DE426D04EE231" }, eeaConsentGeography: true);
  #endif
  ```

  The ids reach `RequestConfiguration.TestDeviceIds` and the consent debug settings only while the
  configuration runs on Google's sample units, so a release build never carries them.

Proven against Google Mobile Ads 11.6.0 in a disposable project (`Nexus/artifacts/admob-<date>/`, gate
row in [`docs/RELEASE_GATES.md`](../../../docs/RELEASE_GATES.md)): the plugin's editor placeholders
load, show, pay and close through the provider. Not covered: app-open, native and rewarded
interstitial ads, mediation setup inside AdMob.

## Legacy adapter behind `AdService`

Google AdMob through the Google Mobile Ads Unity plugin, with the User Messaging Platform
consent flow, as an `IAdNetworkAdapter` for `AdService`. The adapter implements every
capability the service knows (`IAdOutcomeAdapter`, `IAdReadinessAdapter`, `IAdConsentAdapter`),
so `RequestReward`, `ReadyChanged`, `PrivacyOptionsRequired` and `ShowPrivacyOptions` work as
`docs/ADAPTERS.md` of the core package describes.

### Install

1. Google Mobile Ads Unity plugin as a UPM package (`com.google.ads.mobile`, 9.0 or newer; the
   adapter was written against 11.6.0) from the OpenUPM registry, plus its External Dependency
   Manager. Add the scoped registry to `Packages/manifest.json`:
   ```json
   "scopedRegistries": [ { "name": "package.openupm.com", "url": "https://package.openupm.com",
     "scopes": [ "com.google.ads.mobile", "com.google.external-dependency-manager" ] } ]
   ```
2. This package by git URL, pinned to a commit of the repository (the full 40-character hash):
   `https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.admob#<commit>`
3. The AdMob app id in **Assets > Google Mobile Ads > Settings**.

The adapter's assembly compiles only while `com.google.ads.mobile` is installed (assembly
definition `versionDefines`, define `NEXUS_ADMOB`); without the plugin the package is inert.
`NEXUS_ADMOB` exists only inside this assembly: in game code `#if NEXUS_ADMOB` is always false
(core `docs/ADAPTERS.md`, "Shipped adapters").

### Use

```csharp
using Nexus.Ads.AdMob;

var config = new AdMobConfig
{
    RewardedAdUnitIdAndroid = "ca-app-pub-xxx/yyy",
    InterstitialAdUnitIdAndroid = "ca-app-pub-xxx/zzz",
    BannerAdUnitIdAndroid = "ca-app-pub-xxx/www",
    MaxAdContentRating = "G",
};
AdMobAdapter.Register(adFactory, config);          // IAdAdapterFactory, provider name "admob"
adService.SetNetworkAdapter(adFactory.CreateAdapter("admob"));

adService.RequestReward("level_end", outcome => { if (outcome == AdOutcome.Rewarded) Grant(); });
if (adService.PrivacyOptionsRequired) menu.ShowPrivacyButton(() => adService.ShowPrivacyOptions());
```

What the adapter does, in order: `Initialize` asks the User Messaging Platform for consent
(the form shows where the law requires it, EEA and UK), starts the SDK once ads may be
requested, keeps one rewarded and one interstitial ad loaded and retries failed loads with a
growing pause (30 s to 10 min); the game stays playable without ads. `ShowBanner(placement,
position)` creates one anchored banner (`position`: `top` or `bottom`). The placement strings
are labels for the game; every placement uses the configured ad unit of its kind.

Testing the consent form outside the EEA: set `DebugGeographyEea = true` and add the phone's
hashed id to `TestDeviceHashedIds` (the SDK prints it in logcat on the first consent request) in a
test build only. With Google's test app id the form is Google's own test message; with the app's
own id it loads only when the AdMob account has a published GDPR message for the app ("Privacy &
messaging"), and without one the update ends with no form and the game runs without ads.

Threads: the plugin invokes its User Messaging Platform listeners on the Android UI thread and,
with `MobileAds.RaiseAdEventsOnUnityMainThread` on, queues the callbacks in
`MobileAdsEventExecutor`, which only runs once its GameObject exists (Google Mobile Ads 11.6.0
creates it in `MobileAds.Initialize`). Consent is asked before the SDK starts, so `Initialize`
creates the executor first (`MobileAdsEventExecutor.Initialize()`); without it the session that
first obtains consent never got past the consent update. The adapter also captures the main thread
in `Initialize` and continues every consent and SDK callback there, so `onInitialized`, `ReadyChanged` and the
`ShowPrivacyOptions` callback reach the game on the main thread; call `Initialize` and
`ShowPrivacyOptions` on the main thread. Each step is logged as `[AdMob] ...` through
`NexusLog.WarnAlways`, which reaches logcat in release builds: a session whose log has no
`[AdMob] SDK initialized` line stopped at the last step printed.

Not covered: mediation setup, app-open and native ads, rewarded interstitials, ad-revenue
reporting (`OnAdPaid` is not forwarded; add it in a subclass if your analytics need it).
