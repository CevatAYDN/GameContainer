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
the registered product catalog.

```csharp
using Nexus.Core;
using Nexus.Core.Services;

public sealed class MonetizationLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindService<IAdService, AdService>();
        builder.BindService<IIapService, IapService>();
        builder.BindInstance(new AdAdapterFactory());
        builder.BindInstance(new IapAdapterFactory());
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default;

    public ValueTask OnStartAsync(CancellationToken ct)
    {
        // Resolve through the context given to the lifecycle; never FindObjectOfType or async void.
        var ads = _context.Resolve<IAdAdapterFactory>();
        ads.RegisterProvider("admob", () => new AdMobAdapter());          // your class, see below
        _context.Resolve<IAdService>().SetNetworkAdapter(ads.CreateAdapter(AdProviderName));

        var store = _context.Resolve<IIapAdapterFactory>();
        store.RegisterProvider("unityiap", () => new UnityIapAdapter());
        var iap = _context.Resolve<IIapService>();
        iap.RegisterProducts(new ProductDefinition { Id = "remove_ads", Type = ProductType.NonConsumable });
        iap.SetStoreAdapter(store.CreateAdapter(StoreProviderName));
        return default;
    }

    public void OnDispose() { }

    // "mock" in the editor and in test builds, the real provider in release builds.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private const string AdProviderName = "mock", StoreProviderName = "mock";
#else
    private const string AdProviderName = "admob", StoreProviderName = "unityiap";
#endif
    private readonly IContext _context; // supplied by your lifecycle host (Root passes it in)
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
| `IAdService.ReadyChanged` | forwarded from `IAdReadinessAdapter.ReadyChanged` | raised once when the adapter's `Initialize` completes |
| `IAdService.PrivacyOptionsRequired`, `ShowPrivacyOptions(onClosed)` | `IAdConsentAdapter` (the adapter owns the consent flow, for example Google UMP) | `false`; `onClosed` runs immediately |
| `IIapService.Purchase(productId, Action<PurchaseOutcome>)` | `IIapOutcomeAdapter.PurchaseWithOutcome` reports `Purchased`, `Cancelled`, `Pending`, `Failed` or `Unavailable` | bool result maps to `Purchased`; `store_unavailable` / `not_initialized` to `Unavailable`; anything else to `Failed` |
| `IIapService.GetLocalizedPrice(productId)` | `IIapStoreInfoAdapter.GetLocalizedPrice` (the store's string) | the catalog's `ProductDefinition.PriceString`, or null |
| `IIapService.IsStoreAvailable`, `StoreChanged` | `IIapStoreInfoAdapter.IsAvailable` / `Changed` | available whenever an adapter is set (without one, only in the editor and development builds); no change events |

`MonetizationContractTests` (EditMode) lock every row of this table against the mock adapters
and bool-only adapters.

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
stay the pattern for the store. A game on `AdMobAdapter` moves with the family's
[MIGRATION.md](../../com.nexus.ads/MIGRATION.md); AdMob itself stays available there as the
`Nexus.Ads.AdMob` provider in `com.nexus.ads.admob`.

## Shipped adapters

Two optional packages in this repository implement the full contract (base adapter plus every
capability) against real SDKs. Each compiles only while its SDK package is installed
(`versionDefines` in the assembly definition), so installing them without the SDK is inert.

The defines `NEXUS_ADMOB` and `NEXUS_UNITY_IAP` exist only inside the adapter assemblies. In game
code `#if NEXUS_ADMOB` is always false and silently removes what it guards from every build,
release included. Game code that must compile without the SDK declares its own define: the same
`versionDefines` entry in the game's assembly definition, or a scripting define in Player
Settings when the code lives in `Assembly-CSharp`.

| Package | SDK | Provider name | Install |
|---|---|---|---|
| `com.nexus.ads.admob` ([README](../../com.nexus.ads.admob/README.md)) | Google Mobile Ads Unity plugin (`com.google.ads.mobile`, OpenUPM) with User Messaging Platform consent | `admob` | git URL `?path=/Nexus/Packages/com.nexus.ads.admob#<commit>`; legacy, kept until its three games moved to the family |
| `com.nexus.iap.unity` ([README](../../com.nexus.iap.unity/README.md)) | Unity IAP 5 (`com.unity.purchasing`, declared dependency) | `unity` | git URL `?path=/Nexus/Packages/com.nexus.iap.unity#<commit>` |

```csharp
AdMobAdapter.Register(adFactory, new AdMobConfig { RewardedAdUnitIdAndroid = "..." });
adService.SetNetworkAdapter(adFactory.CreateAdapter(AdMobAdapter.ProviderName));
UnityIapAdapter.Register(iapFactory);
iapService.SetStoreAdapter(iapFactory.CreateAdapter(UnityIapAdapter.ProviderName));
```

Both were ported from the first shipped Nexus game and are verified by compiling against the
real SDK packages in a disposable project and by tests of their behaviour before initialization
(`Nexus/artifacts/adapters-050/`; the deferred payment and the consent debug settings in
`adapters-20261020/`; see the repository's `docs/RELEASE_GATES.md`). Gigawatt runs
both in its signed Android IL2CPP release build: its device acceptance passed and a Google test
rewarded ad paid out on the phone; with `DebugGeographyEea` and the phone's hashed id its development
build showed the consent form outside the EEA. A price, a purchase and a restore against Google Play are the
open item.

Adapter warnings (an ad that did not load, a store that is not reachable, products not fetched)
go through `NexusLog.WarnAlways`, so they reach the player log (logcat on Android) in release
builds too. `LoggerService` drops `Log` and `LogWarning` there; errors and exceptions always pass.

## Writing an adapter

An adapter translates the SDK's callbacks into the contract. Keep SDK types inside the adapter so
the game code and tests depend only on `IAdService` / `IIapService`. Two rules the services rely on:

- `IAdNetworkAdapter.Initialize` must call `onInitialized` exactly once when ads can be requested;
  until then `AdService` reports nothing available.
- `ShowRewarded` must call `onCompleted(true)` only when the SDK granted the reward, and
  `onCompleted(false)` on every other outcome (closed early, failed, no fill), or the game pays twice
  or never.
- An SDK may call back on a worker thread: the Google Mobile Ads plugin invokes its User Messaging
  Platform listeners on the Android UI thread. Continue on the Unity main thread before touching
  Unity or the game's handlers; the AdMob adapter captures Unity's `SynchronizationContext` in
  `Initialize` and posts every callback to it, and logs each step through `NexusLog.WarnAlways`.
  An SDK may also queue its callbacks behind an object it creates later: Google Mobile Ads queues
  the consent callbacks in `MobileAdsEventExecutor`, created by `MobileAds.Initialize`, so the
  AdMob adapter creates the executor before it asks for consent.

`MonetizationServiceTests` (EditMode) show the services driven through the mock adapters:
interstitial cooldown and availability, product registration and a mock purchase.

## Where the capabilities come from

Gigawatt, the first game built on Nexus, first shipped its own `IRewardedAds` / `IGameStore`
with simulated and real AdMob / Unity IAP implementations instead of `AdService` and `IapService`.
It needed a consent step before the SDK starts (Google User Messaging Platform; EEA/UK) with a
privacy-options entry in the menu, a readiness event instead of polling, structured outcomes
instead of booleans, and the store's localized prices with a change event. The capability
interfaces above carry exactly that contract, the two shipped adapters are ports of its AdMob and
Unity IAP implementations, and Gigawatt now runs on `AdService` / `IapService` with them.
