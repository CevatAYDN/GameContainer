# Nexus AdMob Adapter (`com.nexus.ads.admob`)

> **Who this is for:** level 3, shipping a game with Google AdMob behind Nexus `AdService`.

Google AdMob through the Google Mobile Ads Unity plugin, with the User Messaging Platform
consent flow, as an `IAdNetworkAdapter` for `AdService`. The adapter implements every
capability the service knows (`IAdOutcomeAdapter`, `IAdReadinessAdapter`, `IAdConsentAdapter`),
so `RequestReward`, `ReadyChanged`, `PrivacyOptionsRequired` and `ShowPrivacyOptions` work as
`docs/ADAPTERS.md` of the core package describes.

## Install

1. Google Mobile Ads Unity plugin as a UPM package (`com.google.ads.mobile`, 9.0 or newer; the
   adapter was written against 11.6.0) from the OpenUPM registry, plus its External Dependency
   Manager. Add the scoped registry to `Packages/manifest.json`:
   ```json
   "scopedRegistries": [ { "name": "package.openupm.com", "url": "https://package.openupm.com",
     "scopes": [ "com.google.ads.mobile", "com.google.external-dependency-manager" ] } ]
   ```
2. This package by git URL, pinned to a commit of the repository:
   `https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.admob#<commit>`
3. The AdMob app id in **Assets > Google Mobile Ads > Settings**.

The adapter's assembly compiles only while `com.google.ads.mobile` is installed (assembly
definition `versionDefines`, define `NEXUS_ADMOB`); without the plugin the package is inert.

## Use

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

Not covered: mediation setup, app-open and native ads, rewarded interstitials, ad-revenue
reporting (`OnAdPaid` is not forwarded; add it in a subclass if your analytics need it).
