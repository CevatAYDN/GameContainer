# Nexus AdMob (`com.nexus.ads.admob`)

> **Who this is for:** level 3, shipping a game with Google AdMob.

`AdMobProvider` and `UmpConsentFlow` in the assembly `Nexus.Ads.AdMob` (`Runtime/Provider`), the ad
family's `IAdProvider` over `com.nexus.ads`, and `UmpAnalyticsConsent` in `Nexus.Ads.AdMob.Firebase`
(`Runtime/Firebase`), the analytics consent `com.nexus.firebase` reads; changes go to the family's
[CHANGELOG](../com.nexus.ads/CHANGELOG.md).
The legacy `AdMobAdapter` behind `com.nexus.core`'s `AdService` was removed once Gigawatt, Wickling and
ChipCrisis had moved to the family (issue #14); a game still on it moves with [MIGRATION.md](../com.nexus.ads/MIGRATION.md)
before it re-pins.

## AdMob provider for the ad family

`Nexus.Ads.AdMob` compiles only while both `com.google.ads.mobile` 11.3.0 or newer and `com.nexus.ads`
are installed (assembly definition `versionDefines` `NEXUS_ADMOB` and `NEXUS_ADS`, both in its
`defineConstraints`), so the package is inert without them.
Install the plugin from OpenUPM (Prepare Android, below, adds the scoped registry
`https://package.openupm.com` with the scope `com.google`; 11.6.0 brings Unity's External Dependency
Manager, `com.unity.external-dependency-manager`, as its own dependency and resolves its Android
libraries during the build), list
`com.nexus.ads` and this package at the same commit, or list `com.nexus.ads` alone and let Prepare
Android add this package at the commit of `com.nexus.ads`; select **AdMob** in **Project Settings > Nexus
Ads**, press **Tools > Nexus Ads > Setup > Prepare Android** (it writes the tab's App ID into the
plugin's settings asset) and put an `AdsBootstrap` in the first scene; the provider registers itself
before the first scene loads. A Nexus game adds `com.nexus.ads.bridge.nexus` and calls `NexusAdsBridge.Connect`.

What it does:
- **Consent:** asks the User Messaging Platform for an update on every launch (Google's privacy page
  requires it) and shows the form where the law needs it; consent kept from an earlier session lets ads
  load while this session's update runs. The privacy options form is `ShowPrivacyOptions`; a withdrawal
  destroys the loaded ads and further loads report "consent does not allow ad requests". The flow is
  `UmpConsentFlow`; the package also registers it with the bootstrap, which runs it before another
  mediation that ships no form (LevelPlay reads the consent UMP stores, issue #63). With LevelPlay selected
  the AdMob provider never starts, the App ID in the plugin's settings asset picks the consent message
  published under AdMob's Privacy & messaging, and the validator fails a build without one (the Android SDK
  stops the app at launch) or with Google's sample App ID on the Release tab. Test devices for that form:
  `UmpConsentFlow.SetDebugDevices(hashedIds, eeaGeography: true)` before the bootstrap starts (and, with
  `UmpAnalyticsConsent` bound, before the context initializes), applied only in a test configuration (a
  mediation's test mode, or Google's sample units).
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

## Analytics consent for `com.nexus.firebase`

`UmpAnalyticsConsent` is the `IAnalyticsConsent` the Firebase services read, over Google's consent form
(issue #87). Bind it with `FirebaseBindings.Bind<UmpAnalyticsConsent>(builder)`; it needs an `IAdService`
in the context (the bridge's). The assembly `Nexus.Ads.AdMob.Firebase` compiles only while
`com.google.ads.mobile` 11.3.0+ and `com.nexus.firebase` are installed (`NEXUS_ADMOB`,
`NEXUS_ADMOB_FIREBASE`); it needs no Firebase SDK.

- **State** is the User Messaging Platform's consent status. UMP has none in a first session (or after
  the app's data is cleared) until an update answers, so at start the reader asks UMP for an update with
  the request the bootstrap's consent flow sends (`UmpConsentFlow.RequestParameters`: the settings' age
  tag, and the debug devices only in a test configuration); a game that starts ads late no longer holds
  its first session's events until the ads start (issue #121). The request comes from the bootstrap's
  settings or the `AdsSettings` asset the bootstrap loads; without either no update is asked and a
  warning is logged. The status is read at start, when the update answers (on the main thread, whichever
  thread UMP answers on) and on `IAdService.ReadyChanged`, which the family raises after the consent
  answer, when ads start or are refused. `Changed` fires only when the status changed, and each change is
  logged in every build (`[Nexus Ads] analytics consent: ...`).
- **Choice** is always `Denied`. Google maps no TCF purpose to `analytics_storage` (purpose 1 maps to
  `ad_storage` and `ad_user_data`; [Implement TCF strings](https://developers.google.com/tag-platform/security/guides/implement-TCF-strings)),
  so the form answers nothing about analytics. Where consent is required (EEA, UK) nothing is sent: no
  analytics and, since #78, no Crashlytics reports. Where it is not required, collection runs. Before
  the first answer, events are held.
- A game that asks its own analytics question binds its own reader instead.

EditMode `UmpAnalyticsConsentTests` (the decision for each UMP status; read again on `ReadyChanged`,
reported only on a change; the update at start and its request), in
`Nexus/artifacts/admob-20261028/AdMobFirebaseConsumer` with Google Mobile Ads 11.6.0. The device line (EEA debug geography: the form shown, collection stays off) is a game's
re-pin run.
