using NUnit.Framework;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #162 (the owner's decision: no mock or fake in what a game plays). Without an adapter the editor used to
    /// show mock ads, grant a mock reward and simulate a purchase; it now answers as a release player does.
    /// </summary>
    [TestFixture]
    public class MonetizationServiceTests
    {
        [Test]
        public async Task AdService_WithoutAnAdapter_ShowsNoAdAndGrantsNoReward_InTheEditorToo()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);

            Assert.IsFalse(ads.IsInterstitialAvailable("default"), "no adapter, no interstitial");
            Assert.IsFalse(ads.IsRewardedAvailable("default"), "no adapter, no rewarded ad");

            LogAssert.Expect(LogType.Warning, new Regex(@"No ad network adapter for interstitial 'default'"));
            bool closed = false;
            ads.ShowInterstitial("default", () => closed = true);
            Assert.IsTrue(closed, "the caller is answered at once");

            LogAssert.Expect(LogType.Warning, new Regex(@"No ad network adapter for rewarded 'default'"));
            bool? rewarded = null;
            ads.ShowRewarded("default", success => rewarded = success);
            Assert.That(rewarded, Is.False, "no reward without an impression");
        }

        [Test]
        public async Task IapService_WithoutAStore_AnswersUnavailableAndOwnsNothing_InTheEditorToo()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.RegisterProducts(new ProductDefinition { Id = "no_ads", Type = ProductType.NonConsumable, PriceString = "$1.99" });

            Assert.IsFalse(iap.IsStoreAvailable, "no store adapter, no store");

            LogAssert.Expect(LogType.Warning, new Regex(@"PurchaseProduct\('no_ads'\) called without a store adapter"));
            bool? purchased = null; string info = null;
            iap.PurchaseProduct("no_ads", (success, detail) => { purchased = success; info = detail; });
            Assert.That((purchased, info), Is.EqualTo(((bool?)false, "store_unavailable")));

            LogAssert.Expect(LogType.Warning, new Regex(@"PurchaseProduct\('no_ads'\) called without a store adapter"));
            PurchaseOutcome? outcome = null;
            iap.Purchase("no_ads", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(PurchaseOutcome.Unavailable));

            LogAssert.Expect(LogType.Warning, new Regex(@"RestorePurchases called without a store adapter"));
            bool? restored = null;
            iap.RestorePurchases(ok => restored = ok);
            Assert.That(restored, Is.False);

            Assert.IsFalse(iap.IsProductOwned("no_ads"), "ownership comes only from a store");
        }

        // Issue #164: the warnings above went through LogWarning, which a release player drops, so a store build without an adapter logged nothing
        [Test]
        public async Task AMissingOrUnknownAdapter_IsWarnedInAReleasePlayerToo()
        {
            NexusRuntime.Reset(); // the fallback logger, which a release player disables; a logger another test left cached may be enabled
            NexusBuild.OverrideForTests(false);
            try
            {
                using var ads = new AdService();
                await ads.InitializeAsync(default);
                using var iap = new IapService();
                await iap.InitializeAsync(default);
                Assert.IsFalse(NexusRuntime.Logger.IsEnabled, "a release player's logger drops LogWarning");

                LogAssert.Expect(LogType.Warning, new Regex(@"No ad network adapter for interstitial 'default'"));
                ads.ShowInterstitial("default");
                LogAssert.Expect(LogType.Warning, new Regex(@"No ad network adapter for rewarded 'default'"));
                ads.ShowRewarded("default", _ => { });
                LogAssert.Expect(LogType.Warning, new Regex(@"PurchaseProduct\('no_ads'\) called without a store adapter"));
                iap.PurchaseProduct("no_ads", (_, _) => { });
                LogAssert.Expect(LogType.Warning, new Regex(@"RestorePurchases called without a store adapter"));
                iap.RestorePurchases(_ => { });
                LogAssert.Expect(LogType.Warning, new Regex(@"Unknown ad provider: 'levelplay'"));
                Assert.IsNull(new AdAdapterFactory().CreateAdapter("levelplay"));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
            }
        }

        private sealed class DisposableStore : IIapStoreAdapter, System.IDisposable
        {
            public int Disposed;
            public void Initialize(System.Collections.Generic.List<ProductDefinition> products, System.Action<bool> onInitialized) => onInitialized?.Invoke(true);
            public void Purchase(string productId, System.Action<bool, string> onComplete) => onComplete?.Invoke(false, "test");
            public void Restore(System.Action<bool> onComplete) => onComplete?.Invoke(false);
            public bool IsOwned(string productId) => false;
            public void Dispose() => Disposed++;
        }

        // Issue #118: a replaced store adapter was never disposed and kept handling Unity IAP's process-wide purchases. Gigawatt sets
        // the same adapter again to restart it, which must not dispose it.
        [Test]
        public void SetStoreAdapter_DisposesTheAdapterItReplaces_NotTheSameAdapterSetAgain()
        {
            using var iap = new IapService();
            var first = new DisposableStore();
            var second = new DisposableStore();
            iap.SetStoreAdapter(first);
            iap.SetStoreAdapter(first);
            Assert.AreEqual(0, first.Disposed, "set again to restart it");
            iap.SetStoreAdapter(second);
            Assert.AreEqual(1, first.Disposed, "replaced");
            Assert.AreEqual(0, second.Disposed);
        }

        private sealed class UnrequestedStore : IIapStoreAdapter, IIapUnrequestedPurchaseAdapter
        {
            public event UnrequestedPurchaseHandler UnrequestedPurchase;
            public bool Handled => UnrequestedPurchase != null;
            public void Report(string productId, string transactionId) => UnrequestedPurchase?.Invoke(productId, transactionId);
            public void Initialize(System.Collections.Generic.List<ProductDefinition> products, System.Action<bool> onInitialized) => onInitialized?.Invoke(true);
            public void Purchase(string productId, System.Action<bool, string> onComplete) => onComplete?.Invoke(false, "test");
            public void Restore(System.Action<bool> onComplete) => onComplete?.Invoke(false);
            public bool IsOwned(string productId) => false;
        }

        // Issue #204: an adapter keeps a paid consumable with the store while nothing grants it, so the service must be subscribed
        // to the adapter only while the game handles UnrequestedPurchase, and follow the adapter it is given
        [Test]
        public void UnrequestedPurchase_ReachesTheAdapter_OnlyWhileTheGameHandlesIt()
        {
            using var iap = new IapService();
            var first = new UnrequestedStore();
            iap.SetStoreAdapter(first);
            Assert.IsFalse(first.Handled, "the game handles nothing: the adapter keeps the order");

            var heard = new System.Collections.Generic.List<string>();
            UnrequestedPurchaseHandler grant = (product, transaction) => heard.Add(product + " " + transaction);
            iap.UnrequestedPurchase += grant;
            Assert.IsTrue(first.Handled);
            first.Report("coins_100", "tx-1");

            var second = new UnrequestedStore();
            iap.SetStoreAdapter(second);
            Assert.IsFalse(first.Handled, "the replaced adapter");
            Assert.IsTrue(second.Handled, "the new adapter");
            iap.UnrequestedPurchase -= grant;
            Assert.IsFalse(second.Handled, "the game's last handler is gone");
            CollectionAssert.AreEqual(new[] { "coins_100 tx-1" }, heard);
        }

        private sealed class StoreThatDoesNotStart : IIapStoreAdapter
        {
            public void Initialize(System.Collections.Generic.List<ProductDefinition> products, System.Action<bool> onInitialized) => onInitialized?.Invoke(false);
            public void Purchase(string productId, System.Action<bool, string> onComplete) => onComplete?.Invoke(false, "not started");
            public void Restore(System.Action<bool> onComplete) => onComplete?.Invoke(false);
            public bool IsOwned(string productId) => false;
        }

        // Issue #164: the store's start went to an info line, which a release player drops, so a store that did not start left no trace
        [Test]
        public async Task AStoreThatDoesNotStart_IsWarnedInAReleasePlayerToo()
        {
            NexusRuntime.Reset(); // the fallback logger, which a release player disables; a logger another test left cached may be enabled
            NexusBuild.OverrideForTests(false);
            try
            {
                using var iap = new IapService();
                await iap.InitializeAsync(default);
                Assert.IsFalse(NexusRuntime.Logger.IsEnabled, "a release player's logger drops Log and LogWarning");

                LogAssert.Expect(LogType.Warning, new Regex(@"\[IapService\] Store adapter initialized: False"));
                iap.SetStoreAdapter(new StoreThatDoesNotStart());
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
            }
        }

        // Issue #183: a false IsProductOwned meant both "not owned" and "the store has not said yet", so a player who bought
        // remove_ads and opened the game offline, or before Play Billing returned purchases, was shown interstitials.
        [Test]
        public async Task Ownership_IsUnknown_UntilTheStoreAnswersWhatThePlayerOwns()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.RegisterProducts(new ProductDefinition { Id = "no_ads", Type = ProductType.NonConsumable });
            Assert.AreEqual(Ownership.Unknown, iap.GetOwnership("no_ads"), "no store yet");

            var store = new PricedStoreAdapter { OwnershipKnown = false };
            iap.SetStoreAdapter(store);
            Assert.AreEqual(Ownership.Unknown, iap.GetOwnership("no_ads"), "the store's purchase fetch has not answered");
            Assert.IsFalse(iap.IsProductOwned("no_ads"));

            store.OwnershipKnown = true;
            Assert.AreEqual(Ownership.NotOwned, iap.GetOwnership("no_ads"));
            store.Owned.Add("no_ads");
            Assert.AreEqual(Ownership.Owned, iap.GetOwnership("no_ads"));
            Assert.IsTrue(iap.IsProductOwned("no_ads"));
        }

        // Issue #183: a store without store info whose Initialize answered false still read as available.
        [Test]
        public async Task AStoreThatDidNotStart_ReadsAsUnavailable_AndItsOwnershipAsUnknown()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.RegisterProducts(new ProductDefinition { Id = "no_ads", Type = ProductType.NonConsumable });

            LogAssert.Expect(LogType.Warning, new Regex(@"\[IapService\] Store adapter initialized: False"));
            iap.SetStoreAdapter(new StoreThatDoesNotStart());

            Assert.IsFalse(iap.IsStoreAvailable);
            Assert.AreEqual(Ownership.Unknown, iap.GetOwnership("no_ads"));
        }

        // Issue #230: the mock owned a bought consumable, which the Unity IAP adapter never does, so a game's test passed on
        // the mock and failed on the real store.
        [Test]
        public void TheMockStore_OwnsABoughtNonConsumable_AndNeverABoughtConsumable()
        {
            var store = new MockIapStoreAdapter();
            store.Initialize(new List<ProductDefinition>
            {
                new ProductDefinition { Id = "coins", Type = ProductType.Consumable },
                new ProductDefinition { Id = "no_ads", Type = ProductType.NonConsumable },
            }, null);

            store.Purchase("coins", null);
            store.Purchase("no_ads", null);

            Assert.IsFalse(store.IsOwned("coins"));
            Assert.IsTrue(store.IsOwned("no_ads"));
        }

        // Issue #183: a product registered after the store started never reached it, and nothing said so.
        [Test]
        public async Task AProductRegisteredAfterTheStoreStarted_IsAnError()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.SetStoreAdapter(new MockIapStoreAdapter());

            LogAssert.Expect(LogType.Error, new Regex(@"RegisterProducts\(late\) after SetStoreAdapter"));
            iap.RegisterProducts(new ProductDefinition { Id = "late", Type = ProductType.Consumable });
        }
    }
}
