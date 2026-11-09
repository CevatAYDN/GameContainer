# Nexus Unity IAP Adapter (`com.nexus.iap.unity`)

> **Who this is for:** level 3, shipping a game with Unity IAP (Google Play, App Store) behind Nexus `IapService`.

Unity IAP 5 as an `IIapStoreAdapter` for `IapService`, with the capabilities the service
knows (`IIapOutcomeAdapter`, `IIapStoreInfoAdapter`): `Purchase` with a `PurchaseOutcome`,
`GetLocalizedPrice` from the store, `IsStoreAvailable` and `StoreChanged`.

## Install

This package by git URL, pinned to a commit of the repository (the full 40-character hash):
`https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.iap.unity#<commit>`.
It depends on `com.unity.purchasing` 5.4.4 (installed by the Package Manager). Unity Gaming
Services are not started by the adapter; Google Play and App Store purchases do not need them.

## Use

```csharp
using Nexus.Iap.Unity;

UnityIapAdapter.Register(iapFactory);               // IIapAdapterFactory, provider name "unity"
iapService.RegisterProducts(new ProductDefinition { Id = "rewards_pass", Type = ProductType.NonConsumable });
iapService.SetStoreAdapter(iapFactory.CreateAdapter("unity"));   // after the products: it hands the catalog to the store

iapService.Purchase("rewards_pass", outcome => { if (outcome == PurchaseOutcome.Purchased) Unlock(); });
label.text = iapService.GetLocalizedPrice("rewards_pass") ?? "...";
iapService.StoreChanged += RefreshShop;
iapService.UnrequestedPurchase += (productId, transactionId) => GrantOnce(productId, transactionId);   // before SetStoreAdapter
```

A paid consumable the game did not request in this session reaches `UnrequestedPurchase` before
the store consumes it: one paid in a session whose process died before the game heard of it
(the store lists it as pending at the next start), a deferred payment approved later, a promo
code. With no handler the adapter does not confirm the order, warns once in every build, and the
store lists it again at the next purchase fetch. The adapter cannot tell an order it never
reported from one the game granted in an earlier session whose confirm failed every retry, so it
reports both: keep the transaction ids you granted and grant each once.

Register every product before `SetStoreAdapter`: it passes the catalog of that moment to the
adapter's `Initialize`, and a product registered later is unknown to the store (`Unavailable`).
Unity IAP's store services are process-wide: an adapter that is replaced must be disposed, or it
keeps handling the store's purchases. `SetStoreAdapter` disposes the adapter it replaces, and
`IapService` disposes its adapter with itself.

Order of events: `Initialize` connects, fetches the catalog's products and the purchases this
account already owns, then confirms every pending order. A start whose connect or product fetch
fails answers false and leaves the store unavailable; the next start connects again. A start whose
product fetch fails still fetches the purchases, so `GetOwnership` answers `Owned` or `NotOwned`
instead of `Unknown` once the store answers (#268). A purchase reports `Purchased` to the
game first and is confirmed with the store right after, so the game records ownership before
acknowledgement. A confirm the store refuses is warned in every build and sent again after 5 s,
30 s and 2 min; after that the next purchase fetch (each start and `Restore`) confirms it.
If the requested delivery callback throws, the adapter does not confirm or remember that
new order as granted (#381). A later fetch offers it through the recovery delivery path.
Keep the grant idempotent and let persistence failures propagate at this synchronous
boundary. Conversely, a successful grant whose store confirmation fails is remembered
in the current session, so confirmation retries do not grant it again. Across restarts,
the game's durable transaction-id ledger remains necessary.
`IsConfirmationPending(productId)` is true until the store confirms the purchase (Google Play
refunds a purchase not acknowledged within three days), and `Changed` is raised when it turns
false. Consumables are never reported as owned; non-consumables and subscriptions are
owned once their order is confirmed or fetched. A purchase fetch only adds ownership and never removes
it, because a successful store fetch can be empty or partial (StoreKit 1 empty receipt, StoreKit 2
skipped unverified transactions); refunds and revocations are a server-side concern. `Restore` asks
the store to restore, and Unity IAP fetches the purchases once after it. A purchase is answered only
by an order for its own product:
an order for another product (a deferred payment approved, a promo code) is owned and raises
`Changed`, and the waiting purchase still gets its own answer. A purchase
whose payment the store holds (deferred) reports `Pending`, and buying that product again answers
`Pending` without a second store call until the order completes or a fetch no longer lists it.

Not covered: receipt validation on a server, subscription details, Apple-specific services.
