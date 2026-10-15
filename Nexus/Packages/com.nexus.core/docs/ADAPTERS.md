# Ad and store adapters

`AdService` and `IapService` carry no SDK dependency. Each talks to one adapter interface; the
package ships a mock adapter for both and a factory that creates adapters by provider name. A game
registers its real adapter with the factory and hands the created adapter to the service. Every
type below is in `Nexus.Core.Services`.

| Service | Adapter contract | Factory | Built-in provider |
|---|---|---|---|
| `AdService` (`IAdService`) | `IAdNetworkAdapter`: `Initialize(Action)`, `IsInterstitialReady`, `ShowInterstitial`, `IsRewardedReady`, `ShowRewarded`, `ShowBanner`, `HideBanner` | `AdAdapterFactory` (`IAdAdapterFactory`): `RegisterProvider(name, Func<IAdNetworkAdapter>)`, `CreateAdapter(name)` | `"mock"` → `MockAdNetworkAdapter` |
| `IapService` (`IIapService`) | `IIapStoreAdapter`: `Initialize(List<ProductDefinition>, Action<bool>)`, `Purchase`, `Restore`, `IsOwned` | `IapAdapterFactory` (`IIapAdapterFactory`) | `"mock"` → `MockIapStoreAdapter` |

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

## What a shipped game needed beyond this

Gigawatt, the first game built on Nexus, does not use `AdService` or `IapService`. Its own
contracts (`IRewardedAds`, `IGameStore` in `Assets/Scripts/Game/Services/RewardOffers.cs`, with
simulated and real AdMob / Unity IAP implementations) add what the package contracts lack:

- a consent step before the SDK starts (Google User Messaging Platform; EEA/UK), and a
  `PrivacyOptionsRequired` flag so the menu can reopen the consent form;
- a `ReadyChanged` event instead of polling `IsRewardedReady`, and a structured `AdOutcome`
  (rewarded, closed, failed, not ready) instead of a `bool`;
- the store's localized price string per product and a `Changed` event after start or restore.

Until the package services offer that contract, treat them as development scaffolding: the mock
adapters are good for tests and prototypes, and a release game should expect to own its
monetization interfaces. This gap is tracked in the repository's `docs/RELEASE_GATES.md`.
