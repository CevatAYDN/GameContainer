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
```

Register every product before `SetStoreAdapter`: it passes the catalog of that moment to the
adapter's `Initialize`, and a product registered later is unknown to the store (`Unavailable`).
Unity IAP's store services are process-wide: an adapter that is replaced must be disposed, or it
keeps handling the store's purchases. `SetStoreAdapter` disposes the adapter it replaces, and
`IapService` disposes its adapter with itself.

Order of events: `Initialize` connects, fetches the catalog's products and the purchases this
account already owns, then confirms every pending order. A start whose connect or product fetch
fails answers false and leaves the store unavailable; the next start connects again. A purchase reports `Purchased` to the
game first and is confirmed with the store right after, so the game records ownership before
acknowledgement. A confirm the store refuses is warned in every build and sent again after 5 s,
30 s and 2 min; after that the next purchase fetch (each start and `Restore`) confirms it.
`IsConfirmationPending(productId)` is true until the store confirms the purchase (Google Play
refunds a purchase not acknowledged within three days), and `Changed` is raised when it turns
false. Consumables are never reported as owned; non-consumables and subscriptions are
owned once their order is confirmed or fetched. `Restore` asks the store to restore, and Unity IAP
fetches the purchases once after it. A purchase is answered only by an order for its own product:
an order for another product (a deferred payment approved, a promo code) is owned and raises
`Changed`, and the waiting purchase still gets its own answer. A purchase
whose payment the store holds (deferred) reports `Pending`, and buying that product again answers
`Pending` without a second store call until the order completes or a fetch no longer lists it.

Not covered: receipt validation on a server, subscription details, Apple-specific services.
