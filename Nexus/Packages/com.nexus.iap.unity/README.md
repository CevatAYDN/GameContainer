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
iapService.SetStoreAdapter(iapFactory.CreateAdapter("unity"));
iapService.RegisterProducts(new ProductDefinition { Id = "rewards_pass", Type = ProductType.NonConsumable });

iapService.Purchase("rewards_pass", outcome => { if (outcome == PurchaseOutcome.Purchased) Unlock(); });
label.text = iapService.GetLocalizedPrice("rewards_pass") ?? "...";
iapService.StoreChanged += RefreshShop;
```

Order of events: `Initialize` connects, fetches the catalog's products and the purchases this
account already owns, then confirms every pending order. A purchase reports `Purchased` to the
game first and is confirmed with the store right after, so the game records ownership before
acknowledgement. Consumables are never reported as owned; non-consumables and subscriptions are
owned once their order is confirmed or fetched. `Restore` fetches purchases again. A purchase
whose payment the store holds (deferred) reports `Pending`, and buying that product again answers
`Pending` without a second store call until the order completes or a fetch no longer lists it.

Not covered: receipt validation on a server, subscription details, Apple-specific services.
