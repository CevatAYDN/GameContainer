# Nexus Ads

The ad package family's core: an engine-free ad core, a Unity layer, an editor setup wizard and
validator (coming with [#11](https://github.com/CevatAYDN/GameContainer/issues/11)), and a simulator
provider. Mediation SDKs live in separate provider packages (`com.nexus.ads.levelplay`,
`com.nexus.ads.max`, `com.nexus.ads.admob`); Nexus games connect through `com.nexus.ads.bridge.nexus`
([#12](https://github.com/CevatAYDN/GameContainer/issues/12)). The plan, decisions and phases are in
[issue #8](https://github.com/CevatAYDN/GameContainer/issues/8); changes are in [CHANGELOG.md](CHANGELOG.md).

## What is here

| Folder | Assembly | Content |
|---|---|---|
| `Runtime/Core` | `Nexus.Ads.Core` (`noEngineReferences: true`, no references) | Placements, `AdPolicy` floors, `PolicyGuard`, `AdController`, `RetryPolicy`, `ProviderGate`, `MediationLock`, `SimulatorProvider`, the ports in `Ports.cs`. Compiled and proven in the host harness (`dotnet run --project tools/nexus-benchmark`, suite `AdsCoreSuite`). |
| `Runtime/Unity` | `Nexus.Ads.Unity` (references Core and `UnityEngine.UI`) | `AdsSettings` asset, `ProviderStatusResolver`, `AdsBootstrap`, the static `Ads` facade, `SimulatorOverlay`, `AdRewardedButton`, the port implementations. |
| `Editor` | (data only) | `PlatformCatalog.json`: links, official test data with source, SDK distribution, setup steps EN + TR. |
| `docs` | | [POLICY_COMPLIANCE.md](docs/POLICY_COMPLIANCE.md) ([Türkçe](docs/POLICY_COMPLIANCE_TR.md)): every platform rule and where the package covers it. |
| `Tests/Editor`, `Tests/Runtime` | `com.nexus.ads.editor.tests`, `com.nexus.ads.tests` | EditMode tests of the settings and the resolver; PlayMode tests of the bootstrap, overlay, scheduler and button. |

## Rules the core enforces

`PolicyGuard` answers every show with one of the rule ids in `PolicyRules`; a block raises a
`PolicyViolation` event, logged as an error in the editor and development builds and reported to
telemetry in release builds. The guard cannot be turned off; the floors in `AdPolicy` can only be
tightened, by the settings, a game or remote configuration alike. The kill switch and the ad-free
entitlement are not violations: they answer `NotReady`. The policies behind each rule, with the
pages they were read from, are in the register.

| Rule | Blocks |
|---|---|
| `UnknownPlacement` | a placement name not declared in the settings, or declared with another format |
| `InterstitialNaturalBreakOnly` | an interstitial on a placement without `NaturalBreak` |
| `InterstitialFrequency` | an interstitial inside the gap, in the first seconds of a session, over a cap, right after a rewarded ad, or inside the placement's own cooldown and caps |
| `NoStackedAds` | a second full-screen ad while one is open |
| `NoAdInBackground` | a show while the app is not visible |
| `NoRequestBeforeConsent` | a show before the consent flow finished |
| `SingleMediation` | a second mediation provider in the process |
| `TestModeRequired`, `UnregisteredDevice` | decided by `ProviderGate` before the SDK starts (see below) |
| `RewardedOnRequestOnly`, `ChildSettingsForwarded`, `NoAutomaticRequests`, `NoAdViewInterference` | structural: the API has no way to break them; the harness proves the shape |

Defaults of `AdPolicy.Default`: 30 s between interstitials, none in the first 60 s of a session,
none within 30 s after a rewarded ad, no session or day cap, a new session after 30 min in the
background. Locked by `AdsCoreSuite` AP1–AP7.

## Which build talks to which SDK

`ProviderGate.Evaluate(build, providerConfig, deviceAdvertisingId)`:

| Build | Configuration | Result |
|---|---|---|
| Editor | any | simulator |
| Release | test mode, test devices or sample units present | blocked (`TestModeRequired`): the validator stops the build first |
| Release | live keys | the SDK |
| Development | Google's sample units | the SDK on any device |
| Development | test mode on, device in the Test tab's list | the SDK with test ads |
| Development | test mode on, device not listed (or list empty) | simulator (`UnregisteredDevice`) |
| Development | test mode off | blocked (`TestModeRequired`) |

So a development build handed to a teammate never requests a live ad.

## Using it without Nexus

1. Create the settings asset: **Assets > Create > Nexus > Ads Settings**, saved as
   `Assets/Resources/NexusAdsSettings.asset`. Declare the placements (name, format, `NaturalBreak`,
   cooldown, caps), the privacy flags and, on the Release tab, the live keys. The Test tab holds the
   provider's test data and the device list (the wizard of #11 fills it).
2. Put `AdsBootstrap` on a GameObject in the first scene (it survives scene loads). Without a
   provider package installed, or in the editor, the simulator runs with a full-screen overlay.
3. Show ads through the facade; placements are strings for now, the generated `AdPlacements`
   constants arrive with #11:

```csharp
using Nexus.Ads;
using Nexus.Ads.Unity;

if (Ads.IsReady("shop_refill"))
    Ads.Show("shop_refill", result => { if (result == AdResult.Rewarded) Wallet.Add(50); });

Ads.Show("level_end"); // interstitial: NaturalBreak placement, paced by the guard
```

Or drop `AdRewardedButton` on a uGUI Button with a `CanvasGroup`: it hides itself while nothing can
show and raises `OnRewarded` once per paid ad.

Full-screen ads pause `AudioListener` and set `Time.timeScale` to 0 until they close (switches on
the bootstrap). Ad events reach `AdsBootstrap.Instance.Telemetry.AddSink(...)`.

## Using it with Nexus

Through `com.nexus.ads.bridge.nexus` (#12): the bridge wraps the controller as the
`IAdNetworkAdapter` of `com.nexus.core`'s `AdService`, so game code written against `IAdService`
does not change.
