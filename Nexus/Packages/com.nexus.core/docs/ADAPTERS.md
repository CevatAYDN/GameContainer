> **Who this is for:** level 3, wiring real ad and store SDKs behind the services.

# Ad and store adapters

`AdService` and `IapService` carry no SDK dependency. Each talks to one adapter interface; the
package ships a mock adapter for both and a factory that creates adapters by provider name. A game
registers its real adapter with the factory and hands the created adapter to the service. Every
type below is in `Nexus.Core.Services`.

| Service | Adapter contract | Factory | Built-in provider |
|---|---|---|---|
| `AdService` (`IAdService`) | `IAdNetworkAdapter`: `Initialize(Action)`, `IsInterstitialReady`, `ShowInterstitial`, `IsRewardedReady`, `ShowRewarded`, `ShowBanner`, `HideBanner`; optional `IAdOutcomeAdapter`, `IAdReadinessAdapter`, `IAdConsentAdapter` | `AdAdapterFactory` (`IAdAdapterFactory`): `RegisterProvider(name, Func<IAdNetworkAdapter>)`, `CreateAdapter(name)` | `"mock"` → `MockAdNetworkAdapter` (implements every capability) |
| `IapService` (`IIapService`) | `IIapStoreAdapter`: `Initialize(List<ProductDefinition>, Action<bool>)`, `Purchase`, `Restore`, `IsOwned`; optional `IIapOutcomeAdapter`, `IIapStoreInfoAdapter` | `IapAdapterFactory` (`IIapAdapterFactory`) | `"mock"` → `MockIapStoreAdapter` (implements every capability) |

Provider names are case-insensitive. `CreateAdapter` returns `null` and logs a warning for an
unknown name, and `null` with an error when the creator throws.

## Wiring in a lifecycle

Bind the services and factories in `OnConfigure`, then select the adapter once the context is
built. `AdService.SetNetworkAdapter` calls the adapter's `Initialize` itself (outside the service
lock, because SDKs may call back synchronously); `IapService.SetStoreAdapter` does the same with
the registered product catalog. The lifecycle below is a component on the Root's GameObject:
`Root` registers every `IContextLifecycle` component there and exposes the context it built as
`Root.Context`.

```csharp
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

[RequireComponent(typeof(Root))]
public sealed class MonetizationLifecycle : MonoBehaviour, IContextLifecycle
{
    // Set before OnInitializeAsync (CANONICAL-PATTERNS, "Canonical Context Lifecycle").
    [Inject] public IAdAdapterFactory AdFactory { get; set; }
    [Inject] public IAdService Ads { get; set; }
    [Inject] public IIapAdapterFactory StoreFactory { get; set; }
    [Inject] public IIapService Iap { get; set; }

    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindService<IAdService, AdService>();
        builder.BindService<IIapService, IapService>();
        // Bind under the interface: BindInstance(new AdAdapterFactory()) would register the concrete type only.
        builder.BindInstance<IAdAdapterFactory>(new AdAdapterFactory());
        builder.BindInstance<IIapAdapterFactory>(new IapAdapterFactory());
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default;

    public ValueTask OnStartAsync(CancellationToken ct)
    {
        AdFactory.RegisterProvider("mynetwork", () => new MyAdNetworkAdapter());   // your class, see "Writing an adapter"
        Ads.SetNetworkAdapter(AdFactory.CreateAdapter(AdProviderName));

        StoreFactory.RegisterProvider("unityiap", () => new UnityIapAdapter());
        Iap.RegisterProducts(new ProductDefinition { Id = "remove_ads", Type = ProductType.NonConsumable });
        Iap.SetStoreAdapter(StoreFactory.CreateAdapter(StoreProviderName));
        return default;
    }

    public void OnDispose() { }

    // "mock" in the editor and in test builds, the real provider in release builds.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private const string AdProviderName = "mock", StoreProviderName = "mock";
#else
    private const string AdProviderName = "mynetwork", StoreProviderName = "unityiap";
#endif
}
```

`MockIapStoreAdapter.Purchase` succeeds only in the editor and development builds; in a release
build it reports `store_unavailable`, so a release build with the mock cannot grant products.

## Capabilities a game relies on

The base contracts answer with booleans. The services expose what a shipped game asks for,
and degrade predictably when an adapter lacks the capability:

| Service member | With the capability | Without it |
|---|---|---|
| `IAdService.RequestReward(placement, Action<AdOutcome>)` | `IAdOutcomeAdapter.RequestReward` reports `Rewarded`, `Closed`, `NotReady` or `Failed` | `NotReady` when `IsRewardedReady` is false (the adapter is not called); otherwise the bool result maps to `Rewarded` / `Closed` |
| `IAdService.ReadyChanged` | forwarded from `IAdReadinessAdapter.ReadyChanged`; also raised once when `AdService`'s own interstitial cooldown ends, if an `ITickService` is bound (issue #99) | raised once when the adapter's `Initialize` completes, and at the end of the interstitial cooldown as on the left |
| `IAdService.PrivacyOptionsRequired`, `ShowPrivacyOptions(onClosed)` | `IAdConsentAdapter` (the adapter owns the consent flow, for example Google UMP) | `false`; `onClosed` runs immediately |
| `IIapService.Purchase(productId, Action<PurchaseOutcome>)` | `IIapOutcomeAdapter.PurchaseWithOutcome` reports `Purchased`, `Cancelled`, `Pending`, `Failed` or `Unavailable` | bool result maps to `Purchased`; `store_unavailable` / `not_initialized` to `Unavailable`; anything else to `Failed` |
| `IIapService.GetLocalizedPrice(productId)` | `IIapStoreInfoAdapter.GetLocalizedPrice` (the store's string) | the catalog's `ProductDefinition.PriceString`, or null |
| `IIapService.IsStoreAvailable`, `StoreChanged` | `IIapStoreInfoAdapter.IsAvailable` / `Changed` | available whenever an adapter is set (without one, only in the editor and development builds); no change events |

`MonetizationContractTests` (EditMode) lock every row of this table against the mock adapters
and bool-only adapters; the event at the end of the cooldown is harness check D4b.

`RequestRewardAsync(placement, ct)` and `PurchaseAsync(productId, ct)` (`MonetizationAsyncExtensions`) are the
same two calls as tasks an `IAsyncCommand` awaits; a callback would arrive after a pooled command has
returned and its `[Inject]` members have been cleared. Cancelling stops the wait, not the ad or the store:
a reward or a purchase that completes afterwards is logged as a warning and not handed to the caller, so
grant a non-consumable from `IsProductOwned`. `MonetizationAsyncTests` (EditMode) lock them.

## The ad package family (recommended for ads)

`com.nexus.ads` with `com.nexus.ads.bridge.nexus` replaces a hand-written ad adapter: the bridge is
the `IAdNetworkAdapter` (with every capability) over the family's controller, and the mediation SDK
sits behind the family's provider packages. The editor runs the family's simulator, a development
build runs the real SDK with the **Test** tab (test mode and the test device list; an unlisted device
gets the simulator), a release build runs the **Release** tab. The provider is never chosen by a
`#if` in game code: the settings asset and `ProviderGate` decide. See the
[ads package README](../../com.nexus.ads/README.md) and the
[bridge README](../../com.nexus.ads.bridge.nexus/README.md); the lifecycle above becomes
`NexusAdsBridge.Connect(ads, adFactory, iap, "remove_ads")` in `OnStartAsync` and loses its
`AdProviderName` switch. The `"mock"` adapter and the `#if UNITY_EDITOR || DEVELOPMENT_BUILD` switch
stay the pattern for the store. AdMob is the family's `Nexus.Ads.AdMob` provider in
`com.nexus.ads.admob`; the legacy `AdMobAdapter` behind `AdService` was removed (issue #14), and a game
still on it moves with the family's [MIGRATION.md](../../com.nexus.ads/MIGRATION.md).

## Shipped adapters

One optional package in this repository implements the full store contract (base adapter plus every
capability) against a real SDK. It compiles only while its SDK package is installed
(`versionDefines` in the assembly definition), so installing it without the SDK is inert. With the SDK
installed below the expression the adapter is compiled out as well; the core's `VersionDefineGuard` then logs an
error after each compile and stops the player build, naming both versions (issue #143). Ads go
through the ad family (above).

The define `NEXUS_UNITY_IAP` exists only inside the adapter assembly, as the ad family's `NEXUS_ADMOB`
and `NEXUS_LEVELPLAY` exist only inside its provider assemblies. In game code `#if NEXUS_ADMOB` is always false and silently removes what it guards from every build,
release included. Game code that must compile without the SDK declares its own define: the same
`versionDefines` entry in the game's assembly definition, or a scripting define in Player
Settings when the code lives in `Assembly-CSharp`.

| Package | SDK | Provider name | Install |
|---|---|---|---|
| `com.nexus.iap.unity` ([README](../../com.nexus.iap.unity/README.md)) | Unity IAP 5 (`com.unity.purchasing`, declared dependency) | `unity` | git URL `?path=/Nexus/Packages/com.nexus.iap.unity#<commit>` |

```csharp
UnityIapAdapter.Register(iapFactory);
iapService.SetStoreAdapter(iapFactory.CreateAdapter(UnityIapAdapter.ProviderName));
```

It was ported from the first shipped Nexus game and is verified by compiling against the real SDK
package in a disposable project and by tests of its behaviour before initialization
(`Nexus/artifacts/adapters-050/`; the deferred payment in `adapters-20261020/`; see the repository's
`docs/RELEASE_GATES.md`). Gigawatt runs it in its signed Android IL2CPP release build; a price, a
purchase and a restore against Google Play are the open item.

Adapter warnings (an ad that did not load, a store that is not reachable, products not fetched)
go through `NexusLog.WarnAlways`, so they reach the player log (logcat on Android) in release
builds too. `LoggerService` drops `Log` and `LogWarning` there; errors and exceptions always pass.

## Writing an adapter

An adapter translates the SDK's callbacks into the contract. Keep SDK types inside the adapter so
the game code and tests depend only on `IAdService` / `IIapService`. Three rules the services rely on:

- `IAdNetworkAdapter.Initialize` must call `onInitialized` exactly once when ads can be requested;
  until then `AdService` reports nothing available.
- `ShowRewarded` must call `onCompleted(true)` only when the SDK granted the reward, and
  `onCompleted(false)` on every other outcome (closed early, failed, no fill), or the game pays twice
  or never.
- An SDK may call back on a worker thread: the Google Mobile Ads plugin invokes its User Messaging
  Platform listeners on the Android UI thread. Continue on the Unity main thread before touching
  Unity or the game's handlers: capture Unity's `SynchronizationContext` in `Initialize`, post every
  callback to it, and log each step through `NexusLog.WarnAlways`. An SDK may also queue its callbacks
  behind an object it creates later: with `MobileAds.RaiseAdEventsOnUnityMainThread` on, Google Mobile
  Ads queues the consent callbacks in `MobileAdsEventExecutor`, created by `MobileAds.Initialize`, so the
  executor must exist before the consent request. The family's `AdMobProvider` and `UmpConsentFlow`
  leave that switch off and post each callback to the main thread themselves.

`MonetizationServiceTests` (EditMode) show the services driven through the mock adapters:
interstitial cooldown and availability, product registration and a mock purchase.

## Where the capabilities come from

Gigawatt, the first game built on Nexus, first shipped its own `IRewardedAds` / `IGameStore`
with simulated and real AdMob / Unity IAP implementations instead of `AdService` and `IapService`.
It needed a consent step before the SDK starts (Google User Messaging Platform; EEA/UK) with a
privacy-options entry in the menu, a readiness event instead of polling, structured outcomes
instead of booleans, and the store's localized prices with a change event. The capability
interfaces above carry exactly that contract, and the Unity IAP adapter is a port of its store. Its ads
now come from the ad family through the bridge, which keeps `AdService` as the game's API.
