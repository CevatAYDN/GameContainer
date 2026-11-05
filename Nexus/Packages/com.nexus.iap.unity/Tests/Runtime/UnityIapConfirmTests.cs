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

            public bool FailConnect, FailProducts;   // a billing service that does not answer; a product fetch that fails
            public override void Connect()
            {
                if (FailConnect) { State = ConnectionState.Disconnected; ConnectCallback?.OnStoreConnectionFailed(new StoreConnectionFailureDescription("billing service unavailable")); return; }
                State = ConnectionState.Connected;
                ConnectCallback?.OnStoreConnectionSucceeded();
            }
            public override void FetchProducts(IReadOnlyCollection<StoreProduct> products)
            {
                if (FailProducts) { ProductsCallback?.OnProductsFetchFailed(new ProductFetchFailureDescription(ProductFetchFailureReason.ProviderUnavailable, "offline")); return; }
                var found = new List<ProductDescription>();
                foreach (var product in products) found.Add(new ProductDescription(product.storeSpecificId, new ProductMetadata("$1.99", product.id, "", "USD", 1.99m)));
                ProductsCallback?.OnProductsFetched(found);
            }
            public bool HoldPurchases;   // answers the purchase fetch only when the test calls AnswerPurchases
            public override void FetchPurchases() { if (!HoldPurchases) AnswerPurchases(); }
            public void AnswerPurchases(params Order[] orders) => PurchaseFetchCallback?.OnAllPurchasesRetrieved(new List<Order>(orders));
            public string DeferProduct;   // a purchase of this product is deferred (Ask to Buy, a cash payment)
            public bool HoldOrders;       // a purchase waits until the test answers it
            public readonly List<ICart> Deferred = new(), Held = new();
            public override void Purchase(ICart cart)
            {
                if (cart.Items()[0].Product.definition.id == DeferProduct) { Deferred.Add(cart); PurchaseCallback?.OnPurchaseDeferred(new DeferredOrder(cart, new TestOrderInfo("tx-deferred-" + Name))); return; }
                if (HoldOrders) { Held.Add(cart); return; }
                PurchaseCallback?.OnPurchaseSucceeded(new PendingOrder(cart, new TestOrderInfo("tx-" + Name)));
            }
            public void Approve(ICart cart, string transactionId) => PurchaseCallback?.OnPurchaseSucceeded(new PendingOrder(cart, new TestOrderInfo(transactionId)));
            public void Cancel(ICart cart) => PurchaseCallback?.OnPurchaseFailed(new FailedOrder(cart, PurchaseFailureReason.UserCancelled, "cancelled"));
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

        private static UnityIapAdapter AdapterRetryingAfter(TimeSpan delay, int retries)
        {
            var adapter = new UnityIapAdapter { ConfirmRetryDelays = new TimeSpan[retries] };
            for (int i = 0; i < retries; i++) adapter.ConfirmRetryDelays[i] = delay;
            return adapter;
        }

        private static List<NexusProduct> Coins() => new List<NexusProduct> { new NexusProduct { Id = "coins_100", Type = NexusProductType.Consumable } };

        // An order of coins_100 that was paid in an earlier session whose process died before the game heard of it: the store lists
        // it as pending at this session's purchase fetch.
        private static PendingOrder PaidEarlier(string transactionId)
            => new PendingOrder(new Cart(new CartItem(UnityIAPServices.StoreController().GetProductById("coins_100"))), new TestOrderInfo(transactionId));

        private static IEnumerator StartWithPurchasesHeld(ConfirmTestStore store, UnityIapAdapter adapter)
        {
            store.HoldPurchases = true;
            bool? ready = null;
            adapter.Initialize(Coins(), ok => ready = ok);
            yield return Until(() => ready != null, "the store initialized");
            Assert.AreEqual(true, ready);
        }

        private static List<NexusProduct> Pass() => new List<NexusProduct> { new NexusProduct { Id = "rewards_pass", Type = NexusProductType.NonConsumable } };

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
            var logger = Nexus.Core.NexusRuntime.Logger;
            bool loggerWasEnabled = logger.IsEnabled;
            try
            {
                logger.IsEnabled = false; // as in a release player, which drops LogWarning
                var adapter = AdapterRetryingAfter(TimeSpan.FromMilliseconds(1), retries: 1);
                LogAssert.Expect(LogType.Warning, new Regex(@"\[UnityIAP\] The store did not confirm the purchase of rewards_pass \(Unknown: billing service unavailable\)"));
                PurchaseOutcome? outcome = null;
                yield return Buy(adapter, "rewards_pass", o => outcome = o);
                Assert.AreEqual(PurchaseOutcome.Purchased, outcome);
                yield return Until(() => store.Confirms >= 2, "the refused confirm was sent again");
                Assert.AreEqual(2, store.Confirms);
                LogAssert.NoUnexpectedReceived();
            }
            finally { logger.IsEnabled = loggerWasEnabled; UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #172: a game can tell that a purchase it granted is not yet acknowledged with the store
        [UnityTest]
        public IEnumerator AnUnconfirmedPurchase_IsReported_UntilTheStoreConfirmsIt()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            UseTestStore("NexusConfirmLater", failConfirms: 1);
            try
            {
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

        // Issue #118: Unity IAP's store services are process-wide, so an adapter IapService replaced kept handling the store's
        // purchases: it owned the order and sent a second confirm beside the live adapter's.
        [UnityTest]
        public IEnumerator AReplacedAdapter_NoLongerHandlesTheStoresPurchases()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusReplacedAdapter", failConfirms: 0);
            using var service = new IapService();
            try
            {
                service.RegisterProducts(Pass().ToArray());
                var replaced = new UnityIapAdapter();
                var live = new UnityIapAdapter();
                LogAssert.Expect(LogType.Log, "[IapService] Store adapter initialized: True");   // each adapter's start
                LogAssert.Expect(LogType.Log, "[IapService] Store adapter initialized: True");
                service.SetStoreAdapter(replaced);
                yield return Until(() => replaced.IsAvailable, "the first adapter started");
                service.SetStoreAdapter(live);
                yield return Until(() => live.IsAvailable, "the second adapter started");

                PurchaseOutcome? outcome = null;
                service.Purchase("rewards_pass", o => outcome = o);
                yield return Until(() => outcome != null && store.Confirms >= 1, "the purchase answered and was confirmed");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.AreEqual(PurchaseOutcome.Purchased, outcome);
                Assert.AreEqual(1, store.Confirms, "only the live adapter confirmed the order");
                Assert.IsFalse(replaced.IsOwned("rewards_pass"), "the replaced adapter did not handle the order");
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #118: Unity IAP completes Connect's task when the connection fails too, and the adapter took that as connected, so
        // every later start skipped the connect and the store never connected in that session
        [UnityTest]
        public IEnumerator AStoreThatFailedToConnect_IsConnectedByTheNextStart()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusConnectFails", failConfirms: 0);
            store.FailConnect = true;
            try
            {
                var adapter = new UnityIapAdapter();
                LogAssert.Expect(LogType.Warning, new Regex(@"^\[UnityIAP\] Disconnected: billing service unavailable"));
                bool? ready = null;
                adapter.Initialize(Pass(), ok => ready = ok);
                yield return Until(() => ready != null, "the first start answered");
                Assert.AreEqual(false, ready);

                store.FailConnect = false;   // the billing service is back
                ready = null;
                adapter.Initialize(Pass(), ok => ready = ok);
                yield return Until(() => ready != null, "the second start answered");
                Assert.AreEqual(true, ready, "the second start connected");
                Assert.IsTrue(adapter.IsAvailable);
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #118: a start cleared the catalog but not the "products fetched" flag, so a restart whose product fetch failed read
        // as available with products the store never returned
        [UnityTest]
        public IEnumerator ARestartWhoseProductFetchFails_LeavesTheStoreUnavailable()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusRefetchFails", failConfirms: 0);
            try
            {
                var adapter = new UnityIapAdapter();
                bool? ready = null;
                adapter.Initialize(Pass(), ok => ready = ok);
                yield return Until(() => ready != null, "the first start answered");
                Assert.IsTrue(adapter.IsAvailable);

                store.FailProducts = true;
                LogAssert.Expect(LogType.Warning, new Regex(@"^\[UnityIAP\] Products not fetched \(rewards_pass\)"));
                ready = null;
                adapter.Initialize(Pass(), ok => ready = ok);
                yield return Until(() => ready != null, "the restart answered");
                Assert.AreEqual(false, ready);
                Assert.IsFalse(adapter.IsAvailable, "the products of this start were not fetched");
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #182: the waiting purchase took any product's order as its answer: a parent approving rewards_pass told the game it
        // had bought skin_pack, and skin_pack's real answer (cancelled) found no request and was dropped
        [UnityTest]
        public IEnumerator AnOrderForAnotherProduct_DoesNotAnswerTheWaitingPurchase()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusOtherProductsOrder", failConfirms: 0);
            store.DeferProduct = "rewards_pass";
            try
            {
                var adapter = new UnityIapAdapter();
                bool? ready = null;
                adapter.Initialize(new List<NexusProduct>
                {
                    new NexusProduct { Id = "rewards_pass", Type = NexusProductType.NonConsumable },
                    new NexusProduct { Id = "skin_pack", Type = NexusProductType.NonConsumable },
                }, ok => ready = ok);
                yield return Until(() => ready != null, "the store initialized");
                PurchaseOutcome? pass = null;
                adapter.PurchaseWithOutcome("rewards_pass", o => pass = o);
                yield return Until(() => pass != null, "the deferred purchase answered");
                Assert.AreEqual(PurchaseOutcome.Pending, pass);

                store.HoldOrders = true;
                PurchaseOutcome? skin = null;
                adapter.PurchaseWithOutcome("skin_pack", o => skin = o);
                yield return Until(() => store.Held.Count == 1, "the store holds the skin_pack purchase");
                store.Approve(store.Deferred[0], "tx-approved");
                yield return Until(() => adapter.IsOwned("rewards_pass"), "the approved rewards_pass is owned");
                Assert.IsNull(skin, "the approval of rewards_pass is not skin_pack's answer");

                LogAssert.Expect(LogType.Warning, new Regex(@"^\[UnityIAP\] Purchase failed: UserCancelled"));
                store.Cancel(store.Held[0]);
                yield return Until(() => skin != null, "skin_pack answered");
                Assert.AreEqual(PurchaseOutcome.Cancelled, skin);
                Assert.IsFalse(adapter.IsOwned("skin_pack"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #204: the adapter confirmed (consumed) a paid consumable it found pending at a fetch, and nothing told the game, so
        // the player paid and got nothing. With no handler the order now stays with the store.
        [UnityTest]
        public IEnumerator APaidConsumableFoundAtAFetch_WithNothingToGrantIt_IsNotConsumed_AndIsWarned()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusFoundUnhandled", failConfirms: 0);
            try
            {
                var adapter = new UnityIapAdapter();
                yield return StartWithPurchasesHeld(store, adapter);
                LogAssert.Expect(LogType.Warning, new Regex(@"^\[UnityIAP\] A paid purchase of coins_100 \(transaction tx-found\) was not requested in this session and nothing handles UnrequestedPurchase: it is not confirmed"));
                store.AnswerPurchases(PaidEarlier("tx-found"));
                yield return Until(() => adapter.OwnershipKnown, "the purchase fetch answered");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.AreEqual(0, store.Confirms, "the store keeps the order until something grants it");
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #204: the game hears of a paid consumable found pending at a fetch before the store consumes it
        [UnityTest]
        public IEnumerator APaidConsumableFoundAtAFetch_ReachesTheGame_BeforeTheStoreConsumesIt()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusFoundHandled", failConfirms: 0);
            try
            {
                var adapter = new UnityIapAdapter();
                var granted = new List<string>();
                int confirmsWhenGranted = -1;
                adapter.UnrequestedPurchase += (product, transaction) => { granted.Add(product + " " + transaction); confirmsWhenGranted = store.Confirms; };
                yield return StartWithPurchasesHeld(store, adapter);
                store.AnswerPurchases(PaidEarlier("tx-found"));
                yield return Until(() => store.Confirms >= 1, "the order was confirmed");
                CollectionAssert.AreEqual(new[] { "coins_100 tx-found" }, granted);
                Assert.AreEqual(0, confirmsWhenGranted, "the game heard of it before the store consumed it");
                Assert.IsFalse(adapter.IsOwned("coins_100"), "a consumable is never owned");
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }

        // Issue #204: a deferred consumable approved later went through the purchase path with no request waiting, and was consumed
        // without a word. Google Play and the App Store route every pending order a fetch finds through the same path.
        [UnityTest]
        public IEnumerator ADeferredConsumableApprovedLater_ReachesTheGame()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusDeferredConsumable", failConfirms: 0);
            store.DeferProduct = "coins_100";
            try
            {
                var adapter = new UnityIapAdapter();
                var granted = new List<string>();
                adapter.UnrequestedPurchase += (product, transaction) => granted.Add(product + " " + transaction);
                bool? ready = null;
                adapter.Initialize(Coins(), ok => ready = ok);
                yield return Until(() => ready != null, "the store initialized");
                PurchaseOutcome? outcome = null;
                adapter.PurchaseWithOutcome("coins_100", o => outcome = o);
                yield return Until(() => outcome != null, "the purchase answered");
                Assert.AreEqual(PurchaseOutcome.Pending, outcome);

                store.Approve(store.Deferred[0], "tx-approved");
                yield return Until(() => store.Confirms >= 1, "the approved order was confirmed");
                CollectionAssert.AreEqual(new[] { "coins_100 tx-approved" }, granted);
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

        // Issue #268: a start whose product fetch failed never fetched the purchases, so ownership stayed unknown for the session and
        // the ads bridge, which reads unknown as ad-free, showed no interstitial to a player who never bought remove_ads
        [UnityTest]
        public IEnumerator AStartWhoseProductFetchFails_StillLearnsWhatTheAccountOwns()
        {
            string previous = UnityIAPServices.GetDefaultStore();
            var store = UseTestStore("NexusProductsMissing", failConfirms: 0);
            store.FailProducts = true;
            try
            {
                var adapter = new UnityIapAdapter();
                LogAssert.Expect(LogType.Warning, new Regex(@"^\[UnityIAP\] Products not fetched \(remove_ads\)"));
                bool? ready = null;
                adapter.Initialize(new List<NexusProduct> { new NexusProduct { Id = "remove_ads", Type = NexusProductType.NonConsumable } }, ok => ready = ok);
                yield return Until(() => ready != null, "the start answered");
                Assert.AreEqual(false, ready);
                Assert.IsFalse(adapter.IsAvailable, "the products were not fetched");

                yield return Until(() => adapter.OwnershipKnown, "ownership became known");
                Assert.IsFalse(adapter.IsOwned("remove_ads"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityIAPServices.SetStoreAsDefault(previous); }
        }
    }
}
#endif
