#if NEXUS_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.TestTools;
using StoreProduct = UnityEngine.Purchasing.ProductDefinition;
using StoreProductType = UnityEngine.Purchasing.ProductType;

namespace Nexus.Iap.Unity.Tests
{
    /// <summary>Before the store connected every purchase is Unavailable, nothing is owned and no price exists; the registration name is stable;
    /// a failed fetch is logged with the store's reason, not the failure type's name.</summary>
    public sealed class UnityIapAdapterTests
    {
        [Test]
        public void BeforeInitialize_StoreIsUnavailable()
        {
            var adapter = new UnityIapAdapter();
            Assert.IsFalse(adapter.IsAvailable);
            Assert.IsNull(adapter.GetLocalizedPrice("rewards_pass"));
            Assert.IsFalse(adapter.IsOwned("rewards_pass"));
            PurchaseOutcome? outcome = null;
            adapter.PurchaseWithOutcome("rewards_pass", o => outcome = o);
            Assert.AreEqual(PurchaseOutcome.Unavailable, outcome);
            bool? legacy = null; string info = null;
            adapter.Purchase("rewards_pass", (ok, i) => { legacy = ok; info = i; });
            Assert.AreEqual(false, legacy);
            Assert.AreEqual("store_unavailable", info, "the bool contract names the reason IapService maps to Unavailable");
            bool? restored = null;
            adapter.Restore(ok => restored = ok);
            Assert.AreEqual(false, restored);
        }

        [Test]
        public void AfterADeferredPayment_BuyingAgainAnswersPending_WithoutTheStore()
        {
            var adapter = new UnityIapAdapter();
            adapter.Defer("rewards_pass", true);
            PurchaseOutcome? outcome = null;
            adapter.PurchaseWithOutcome("rewards_pass", o => outcome = o);
            Assert.AreEqual(PurchaseOutcome.Pending, outcome, "a held payment is not a failure (the store answered a second call 'already owned')");
            Assert.IsFalse(adapter.IsOwned("rewards_pass"), "a deferred payment unlocks nothing yet");
            adapter.Defer("rewards_pass", false);
            adapter.PurchaseWithOutcome("rewards_pass", o => outcome = o);
            Assert.AreEqual(PurchaseOutcome.Unavailable, outcome, "once settled the purchase goes to the store again (unconnected here)");
        }

        [Test]
        public void Register_UsesTheProviderName()
        {
            var factory = new IapAdapterFactory();
            UnityIapAdapter.Register(factory);
            Assert.IsInstanceOf<UnityIapAdapter>(factory.CreateAdapter(UnityIapAdapter.ProviderName));
        }

        [Test]
        public void ProductsFetchFailure_NamesTheReasonAndTheProducts()
        {
            // ProductFetchFailed has only an internal constructor; the store raises it.
            var failure = (ProductFetchFailed)Activator.CreateInstance(typeof(ProductFetchFailed), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { new List<StoreProduct> { new("rewards_pass", StoreProductType.NonConsumable), new("coins_100", StoreProductType.Consumable) }, "ProductUnavailable" }, null);
            Assert.AreEqual("Products not fetched (rewards_pass, coins_100): ProductUnavailable", UnityIapAdapter.Describe(failure));
        }

        [Test]
        public void PurchasesFetchFailure_NamesTheReasonAndTheMessage()
        {
            var failure = new PurchasesFetchFailureDescription(PurchasesFetchFailureReason.StoreNotConnected, "billing service disconnected");
            Assert.AreEqual("Purchases not fetched: StoreNotConnected billing service disconnected", UnityIapAdapter.Describe(failure));
        }

        // Issue #329: an order was typed only by the catalog, so a consumable removed from it while a purchase of it was pending
        // was owned and confirmed (consumed at the store) without UnrequestedPurchase: the player paid and got nothing.
        [Test]
        public void APendingConsumableMissingFromTheCatalog_IsHandedToUnrequestedPurchase_NotConfirmedAsOwned()
        {
            var adapter = new UnityIapAdapter();
            var order = PendingOrderOf("retired_coins_pack", StoreProductType.Consumable, "tx-retired");
            Invoke(adapter, "Own", order);
            Assert.IsFalse(adapter.IsOwned("retired_coins_pack"), "a consumable is never owned");
            LogAssert.Expect(LogType.Warning, new Regex(@"\[UnityIAP\] A paid purchase of retired_coins_pack \(transaction tx-retired\) was not requested in this session and nothing handles UnrequestedPurchase"));
            Assert.IsFalse((bool)Invoke(adapter, "GrantUnrequested", order), "without a handler the order stays with the store (#204)");
            var granted = new List<string>();
            adapter.UnrequestedPurchase += (id, transaction) => granted.Add(id + "/" + transaction);
            Assert.IsTrue((bool)Invoke(adapter, "GrantUnrequested", order), "confirmed only once the game was told");
            CollectionAssert.AreEqual(new[] { "retired_coins_pack/tx-retired" }, granted);
        }

        // Issue #329: Unity IAP types a product this session did not fetch Unknown (Product.CreateUnknownProduct), and confirming one
        // acknowledges it on Google Play instead of consuming it: it is kept with the store, not granted or confirmed.
        [Test]
        public void APendingPurchaseTheStoreCouldNotType_IsNotConfirmedNorOwned_AndWarnedOncePerOrder()
        {
            var adapter = new UnityIapAdapter();
            int granted = 0;
            adapter.UnrequestedPurchase += (_, _) => granted++;
            var order = PendingOrderOf("retired_sku", StoreProductType.Unknown, "tx-untyped");
            Invoke(adapter, "Own", order);
            Assert.IsFalse(adapter.IsOwned("retired_sku"), "an order that is not confirmed is not owned");
            LogAssert.Expect(LogType.Warning, new Regex(@"\[UnityIAP\] A paid purchase of retired_sku \(transaction tx-untyped\) names a product this session's catalog does not list and the store did not type: it is not confirmed"));
            Assert.IsFalse((bool)Invoke(adapter, "GrantUnrequested", order));
            Assert.IsFalse((bool)Invoke(adapter, "GrantUnrequested", order), "a fetch lists the order again (OnPending and OnFetched)");
            Assert.AreEqual(0, granted, "the adapter cannot tell a consumable from an owned product");
            LogAssert.NoUnexpectedReceived();
        }

        private static object Invoke(UnityIapAdapter adapter, string method, Order order)
            => typeof(UnityIapAdapter).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, new object[] { order });

        // Product's constructor is internal; the store builds it.
        private static PendingOrder PendingOrderOf(string productId, StoreProductType type, string transactionId)
        {
            var product = (Product)Activator.CreateInstance(typeof(Product), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { new StoreProduct(productId, type), new ProductMetadata(), false }, null);
            return new PendingOrder(new Cart(new CartItem(product)), new TestOrderInfo(transactionId));
        }

        // Unity IAP's OrderInfo is internal.
        private sealed class TestOrderInfo : IOrderInfo
        {
            public TestOrderInfo(string transactionId) => TransactionID = transactionId;
            public IAppleOrderInfo Apple => null;
            public IGoogleOrderInfo Google => null;
            public IPaymentProvidersOrderInfo PaymentProviders => null;
            public List<IPurchasedProductInfo> PurchasedProductInfo { get; set; } = new();
            public string Receipt => "receipt";
            public string TransactionID { get; }
        }
    }
}
#endif
