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

## Writing an adapter

An adapter translates the SDK's callbacks into the contract. Keep SDK types inside the adapter so
the game code and tests depend only on `IAdService` / `IIapService`. Two rules the services rely on:

- `IAdNetworkAdapter.Initialize` must call `onInitialized` exactly once when ads can be requested;
  until then `AdService` reports nothing available.
- `ShowRewarded` must call `onCompleted(true)` only when the SDK granted the reward, and
  `onCompleted(false)` on every other outcome (closed early, failed, no fill), or the game pays twice
  or never.

`MonetizationServiceTests` (EditMode) show the services driven through the mock adapters:
interstitial cooldown and availability, product registration and a mock purchase.

## Where the capabilities come from

Gigawatt, the first game built on Nexus, did not use `AdService` or `IapService`: its own
`IRewardedAds` / `IGameStore` (in `Assets/Scripts/Game/Services/RewardOffers.cs`, with simulated
and real AdMob / Unity IAP implementations) needed a consent step before the SDK starts (Google
User Messaging Platform; EEA/UK) with a privacy-options entry in the menu, a readiness event instead
of polling, structured outcomes instead of booleans, and the store's localized prices with a
change event. The capability interfaces above carry exactly that contract. What still lives in
the game's adapter: the consent flow itself (an `IAdConsentAdapter` wraps the SDK's consent
form), and the SDK-specific mapping of events to outcomes; the package ships no AdMob or Unity
IAP adapter.
