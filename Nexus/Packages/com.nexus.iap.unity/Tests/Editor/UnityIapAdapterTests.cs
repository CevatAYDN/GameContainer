#if NEXUS_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine.Purchasing;
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
    }
}
#endif
