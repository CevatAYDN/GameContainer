#if NEXUS_UNITY_IAP
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;
using UnityEngine.TestTools;
using NexusProduct = Nexus.Core.Services.ProductDefinition;
using NexusProductType = Nexus.Core.Services.ProductType;
using StoreProduct = UnityEngine.Purchasing.ProductDefinition;

namespace Nexus.Iap.Unity.Tests
{
    /// <summary>Confirms against a store plugged into Unity IAP, in play mode: Unity IAP sets its store factory before the first
    /// scene loads and keeps an object with DontDestroyOnLoad, so it does not run in an EditMode test.</summary>
    public sealed class UnityIapConfirmTests
    {
        // A store plugged into Unity IAP through its custom-store API (UnityIAPServices.AddNewCustomStore): it connects, knows every
        // product, sells it, and refuses the first FailConfirms confirms the way Google Play does without a connection.
        private sealed class ConfirmTestStore : Store
        {
            public string Name;
            public int FailConfirms, Confirms;
            public ConnectionState State = ConnectionState.Disconnected;

            public override void Connect() { State = ConnectionState.Connected; ConnectCallback?.OnStoreConnectionSucceeded(); }
            public override void FetchProducts(IReadOnlyCollection<StoreProduct> products)
            {
                var found = new List<ProductDescription>();
                foreach (var product in products) found.Add(new ProductDescription(product.storeSpecificId, new ProductMetadata("$1.99", product.id, "", "USD", 1.99m)));
                ProductsCallback?.OnProductsFetched(found);
            }
            public bool HoldPurchases;   // answers the purchase fetch only when the test calls AnswerPurchases
            public override void FetchPurchases() { if (!HoldPurchases) AnswerPurchases(); }
            public void AnswerPurchases() => PurchaseFetchCallback?.OnAllPurchasesRetrieved(new List<Order>());
            public override void Purchase(ICart cart) => PurchaseCallback?.OnPurchaseSucceeded(new PendingOrder(cart, new TestOrderInfo("tx-" + Name)));
            public override void FinishTransaction(PendingOrder order)
            {
                if (++Confirms <= FailConfirms) ConfirmCallback?.OnConfirmOrderFailed(new FailedOrder(order, PurchaseFailureReason.Unknown, "billing service unavailable"));
                else ConfirmCallback?.OnConfirmOrderSucceeded(order.Info.TransactionID);
            }
            public override void CheckEntitlement(StoreProduct product) { }
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

        private sealed class ConfirmTestStoreWrapper : IStoreWrapper
        {
            private readonly ConfirmTestStore _store;
            public ConfirmTestStoreWrapper(ConfirmTestStore store) => _store = store;
            public Store instance => _store;
            public string name => _store.Name;
            public ConnectionState GetStoreConnectionState() => _store.State;
        }

        // Unity IAP keeps a store's services, and every adapter subscribed to them, for the process: each test has its own store.
        private static ConfirmTestStore UseTestStore(string name, int failConfirms)
        {
            var store = new ConfirmTestStore { Name = name, FailConfirms = failConfirms };
            UnityIAPServices.AddNewCustomStore(new ConfirmTestStoreWrapper(store));
            UnityIAPServices.SetStoreAsDefault(name);
            return store;
        }

        // Issue #182: the adapter does not observe OnStoreConnected, and Unity IAP warns about that on every Connect.
        private static void ExpectTheConnectWarning()
            => LogAssert.Expect(LogType.Warning, "InAppPurchasing: IStoreService.Connect called without a callback defined for IStoreService.OnStoreConnected.");

        private static UnityIapAdapter AdapterRetryingAfter(TimeSpan delay, int retries)
        {
            var adapter = new UnityIapAdapter { ConfirmRetryDelays = new TimeSpan[retries] };
            for (int i = 0; i < retries; i++) adapter.ConfirmRetryDelays[i] = delay;
            return adapter;
        }

        private static IEnumerator Until(Func<bool> done, string what)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!done() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(done(), what + " within 5 s");
        }

        private static IEnumerator Buy(UnityIapAdapter adapter, string productId, Action<PurchaseOutcome?> outcome, Action whenTold = null)
        {
            bool? ready = null;
            adapter.Initialize(new List<NexusProduct> { new NexusProduct { Id = productId, Type = NexusProductType.NonConsumable } }, ok => ready = ok);
            yield return Until(() => ready != null, "the store initialized");
            Assert.AreEqual(true, ready);
            PurchaseOutcome? result = null;
            adapter.PurchaseWithOutcome(productId, o => { result = o; whenTold?.Invoke(); });
            yield return Until(() => result != null, "the purchase answered");
            outcome(result);
        }

        // Issue #172: the adapter did not observe OnPurchaseConfirmed, so a confirm the store refused was dropped with no line in any
        // build and not sent again in the session, while Google Play refunds a purchase not acknowledged within three days.
        [UnityTest]
        public IEnumerator AConfirmTheStoreRefuses_IsWarnedInEveryBuild_AndSentAgain()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusConfirmRefused", failConfirms: 1);
            try
            {
                ExpectTheConnectWarning();
                var adapter = AdapterRetryingAfter(TimeSpan.FromMilliseconds(1), retries: 1);
                LogAssert.Expect(LogType.Warning, new Regex(@"\[UnityIAP\] The store did not confirm the purchase of rewards_pass \(Unknown: billing service unavailable\)"));
                PurchaseOutcome? outcome = null;
                yield return Buy(adapter, "rewards_pass", o => outcome = o);
                Assert.AreEqual(PurchaseOutcome.Purchased, outcome);
                yield return Until(() => store.Confirms >= 2, "the refused confirm was sent again");
                Assert.AreEqual(2, store.Confirms);
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #172: a game can tell that a purchase it granted is not yet acknowledged with the store
        [UnityTest]
        public IEnumerator AnUnconfirmedPurchase_IsReported_UntilTheStoreConfirmsIt()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            UseTestStore("NexusConfirmLater", failConfirms: 1);
            try
            {
                ExpectTheConnectWarning();
                var adapter = AdapterRetryingAfter(TimeSpan.FromMilliseconds(200), retries: 1);
                int changed = 0;
                adapter.Changed += () => changed++;
                LogAssert.Expect(LogType.Warning, new Regex(@"The store did not confirm the purchase of rewards_pass .*attempt 1 of 2"));
                PurchaseOutcome? outcome = null;
                bool pendingWhenTold = false;
                yield return Buy(adapter, "rewards_pass", o => outcome = o, () => pendingWhenTold = adapter.IsConfirmationPending("rewards_pass"));
                Assert.AreEqual(PurchaseOutcome.Purchased, outcome);
                Assert.IsTrue(pendingWhenTold, "not yet acknowledged when the game is told Purchased");
                Assert.IsTrue(adapter.IsOwned("rewards_pass"));
                Assert.IsTrue(adapter.IsConfirmationPending("rewards_pass"), "granted, not yet acknowledged");
                int changedBefore = changed;
                yield return Until(() => !adapter.IsConfirmationPending("rewards_pass"), "the second confirm was accepted");
                Assert.Greater(changed, changedBefore, "Changed is raised when the store confirms the purchase");
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #172: after its last retry the adapter says what happens next, once, instead of retrying without end
        [UnityTest]
        public IEnumerator AConfirmRefusedEveryTime_StopsAfterTheLastRetry_AndSaysWhatHappensNext()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusConfirmNever", failConfirms: int.MaxValue);
            try
            {
                ExpectTheConnectWarning();
                var adapter = AdapterRetryingAfter(TimeSpan.FromMilliseconds(1), retries: 2);
                LogAssert.Expect(LogType.Warning, new Regex(@"The store did not confirm the purchase of rewards_pass .*attempt 1 of 3"));
                LogAssert.Expect(LogType.Warning, new Regex(@"The store did not confirm the purchase of rewards_pass .*attempt 2 of 3"));
                LogAssert.Expect(LogType.Warning, new Regex(@"The store did not confirm the purchase of rewards_pass after 3 attempts .*the next purchase fetch \(each start and Restore\) confirms it again; Google Play refunds a purchase not acknowledged within three days"));
                PurchaseOutcome? outcome = null;
                yield return Buy(adapter, "rewards_pass", o => outcome = o);
                yield return Until(() => store.Confirms >= 3, "three confirms were sent");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.AreEqual(3, store.Confirms, "no confirm after the last retry");
                Assert.IsTrue(adapter.IsConfirmationPending("rewards_pass"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #183: before the store answered which products the account owns, every product read as not owned, so a player who
        // bought remove_ads was shown interstitials offline or until Play Billing returned purchases.
        [UnityTest]
        public IEnumerator OwnershipIsUnknown_UntilThePurchaseFetchAnswers()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusOwnershipLater", failConfirms: 0);
            store.HoldPurchases = true;
            try
            {
                ExpectTheConnectWarning();
                var adapter = new UnityIapAdapter();
                bool? ready = null;
                adapter.Initialize(new List<NexusProduct> { new NexusProduct { Id = "remove_ads", Type = NexusProductType.NonConsumable } }, ok => ready = ok);
                yield return Until(() => ready != null, "the store initialized");
                Assert.IsTrue(adapter.IsAvailable, "the products were fetched");
                Assert.IsFalse(adapter.OwnershipKnown, "the purchase fetch has not answered");

                store.AnswerPurchases();
                yield return Until(() => adapter.OwnershipKnown, "ownership became known");
                Assert.IsFalse(adapter.IsOwned("remove_ads"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }
    }
}
#endif
