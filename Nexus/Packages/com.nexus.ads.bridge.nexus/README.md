# Nexus Ads Bridge

Puts the `com.nexus.ads` controller behind `com.nexus.core`'s `AdService`. Game code written against
`IAdService` (`RequestReward`, `ShowInterstitial`, `IsRewardedAvailable`, `ReadyChanged`,
`PrivacyOptionsRequired`, `ShowPrivacyOptions`, `OnImpressionRecorded`) does not change; the
placements, the policy guard, the test and release tabs and the simulator come from the ads package.
Plan and phases: [issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes in the
family's [CHANGELOG](../com.nexus.ads/CHANGELOG.md).

## Install

Both packages by git URL, pinned to the same commit, the full 40-character hash (the ads validator's `manifest.pins` check keeps
them together; **Tools > Nexus Ads > Pin family packages to one commit** rewrites them):

```json
"com.nexus.ads": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#<commit>",
"com.nexus.ads.bridge.nexus": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.bridge.nexus#<commit>"
```

## Wiring

Bind the service and the factory as before, put an `AdsBootstrap` in the first scene (it loads
`Resources/NexusAdsSettings`, created by **Project Settings > Nexus Ads**), and connect once:

```csharp
using Nexus.Ads.Bridge;
using Nexus.Core.Services;

public void OnConfigure(IContextBuilder builder)
{
    builder.BindService<IAdService, AdService>();
    builder.BindInstance<IAdAdapterFactory>(new AdAdapterFactory());
}

public ValueTask OnStartAsync(CancellationToken ct)
{
    // store and product are optional: with them, owning "remove_ads" stops interstitials and keeps rewarded ads
    NexusAdsBridge.Connect(_context.Resolve<IAdService>(), _context.Resolve<IAdAdapterFactory>(), _context.Resolve<IIapService>(), "remove_ads");
    return default;
}
```

`Connect` registers the adapter under `nexus-ads` and under the running provider's name, sets the
service's interstitial cooldown to the policy's interstitial floor, forwards revenue to
`AdService.RaiseImpression` (so `OnImpressionRecorded` fires with the network, the value and the
placement), and completes on the bootstrap's `Started` event when the bootstrap has not fetched the
device id yet.

| `IAdService` member | Through the bridge |
|---|---|
| `IsRewardedAvailable`, `IsInterstitialAvailable` | `AdController.IsReady`: loaded, visible, not paced out by the guard, kill switch off, not ad-free (interstitial) |
| `RequestReward(placement, Action<AdOutcome>)` | `Rewarded`, `Closed`, `Failed`; `NotReady` for nothing loaded and for a guard block (the block is a `PolicyViolation` on the controller, logged in development builds) |
| `ShowRewarded(placement, Action<bool>)` | true only on `Rewarded` |
| `ShowInterstitial` | the guard's natural-break and frequency rules, then the SDK |
| `ShowBanner(placement, position)` | `"top"` or anything else = bottom |
| `ReadyChanged` | the controller's |
| `PrivacyOptionsRequired`, `ShowPrivacyOptions` | the provider's consent flow (UMP for AdMob, the mediation's form for LevelPlay and MAX, the simulator's switch in the editor) |
| `OnImpressionRecorded` | every `AdRevenue` the mediation reports |

Remote configuration: with `com.nexus.firebase` installed, `Nexus.Ads.Bridge.Nexus.Firebase`
compiles `FirebaseAdsRemoteConfig` over `IRemoteConfigService` (`ads.kill_switch` plus the
`AdPolicy.Keys` names; remote can only tighten). Attach it with
`AdsBootstrap.Instance.Controller.SetRemoteConfig(new FirebaseAdsRemoteConfig(remote))`.

A paid reward is written to the store before the game hears of it: check
`AdsBootstrap.Instance.Controller.PendingReward` at start-up, pay it, then `AcknowledgeReward`.

## Tests

`Tests/Editor/NexusAdsBridgeContractTests` drive `AdService` through the adapter over the simulator
with hand-written ports: the capability rows of `com.nexus.core/docs/ADAPTERS.md` (outcomes,
readiness, privacy options, the cooldown floor, revenue, the ad-free entitlement, the outcome map).
`Tests/Runtime/NexusAdsBridgePlayModeTests` connect before the bootstrap started and take a reward
through the service over the editor's simulator overlay.
