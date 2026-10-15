#if NEXUS_UNITY_IAP
using Nexus.Core.Services;
using NUnit.Framework;

namespace Nexus.Iap.Unity.Tests
{
    /// <summary>Before the store connected every purchase is Unavailable, nothing is owned and no price exists; the registration name is stable.</summary>
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
    }
}
#endif
