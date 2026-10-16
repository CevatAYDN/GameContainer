using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core.Services;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    /// <summary>An adapter that only implements the base contracts (no outcome, readiness, consent or store-info capability).</summary>
    internal sealed class BoolOnlyAdAdapter : IAdNetworkAdapter
    {
        public bool Ready = true;
        public bool RewardResult = true;
        public void Initialize(Action onInitialized) => onInitialized?.Invoke();
        public bool IsInterstitialReady(string placement) => Ready;
        public void ShowInterstitial(string placement, Action onClosed) => onClosed?.Invoke();
        public bool IsRewardedReady(string placement) => Ready;
        public void ShowRewarded(string placement, Action<bool> onCompleted) => onCompleted?.Invoke(RewardResult);
        public void ShowBanner(string placement, string position) { }
        public void HideBanner() { }
    }

    internal sealed class PricedStoreAdapter : IIapStoreAdapter, IIapStoreInfoAdapter
    {
        public bool IsAvailable => true;
        public event Action Changed;
        public void RaiseChanged() => Changed?.Invoke();
        public string GetLocalizedPrice(string productId) => productId == "pass" ? "19,99 TL" : null;
        public void Initialize(List<ProductDefinition> products, Action<bool> onInitialized) => onInitialized?.Invoke(true);
        public void Purchase(string productId, Action<bool, string> onComplete) => onComplete?.Invoke(false, "user_cancelled");
        public void Restore(Action<bool> onComplete) => onComplete?.Invoke(true);
        public bool IsOwned(string productId) => false;
    }

    /// <summary>
    /// The contract a shipped game needed from the monetization services: structured outcomes, readiness
    /// and store-change events, localized prices and privacy options, each degrading predictably for
    /// adapters that lack the capability.
    /// </summary>
    public sealed class MonetizationContractTests
    {
        [Test]
        public async Task RewardedOutcome_IsNotReadyWithoutCallingTheAdapter()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            var mock = new MockAdNetworkAdapter();
            ads.SetNetworkAdapter(mock);
            mock.SetRewardedReady("default", false);

            AdOutcome? outcome = null;
            ads.RequestReward("default", o => outcome = o);

            Assert.That(outcome, Is.EqualTo(AdOutcome.NotReady));
        }

        [Test]
        public async Task MockAdapter_EveryPlacementIsReadyUntilOverridden()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            var mock = new MockAdNetworkAdapter();
            ads.SetNetworkAdapter(mock);

            Assert.IsTrue(ads.IsRewardedAvailable("level_end"), "placements are labels, as in the shipped AdMob adapter");
            Assert.IsTrue(ads.IsInterstitialAvailable("level_end"));
            AdOutcome? outcome = null;
            ads.RequestReward("level_end", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(AdOutcome.Rewarded));

            mock.SetRewardedReady("level_end", false);
            Assert.IsFalse(ads.IsRewardedAvailable("level_end"), "an explicit override still wins");
            Assert.IsTrue(ads.IsRewardedAvailable("default"), "an override is per placement");
        }

        [Test]
        public async Task RewardedOutcome_UsesTheAdapterOutcome()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            ads.SetNetworkAdapter(new MockAdNetworkAdapter());

            AdOutcome? outcome = null;
            ads.RequestReward("default", o => outcome = o);

            Assert.That(outcome, Is.EqualTo(AdOutcome.Rewarded));
        }

        [Test]
        public async Task RewardedOutcome_MapsBoolOnlyAdapters()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            var adapter = new BoolOnlyAdAdapter { RewardResult = false };
            ads.SetNetworkAdapter(adapter);

            AdOutcome? outcome = null;
            ads.RequestReward("default", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(AdOutcome.Closed), "false from a bool adapter means closed without reward");

            adapter.RewardResult = true;
            ads.RequestReward("default", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(AdOutcome.Rewarded));
        }

        [Test]
        public async Task ReadyChanged_FollowsTheAdapter()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            int raised = 0;
            ads.ReadyChanged += () => raised++;
            var mock = new MockAdNetworkAdapter();

            ads.SetNetworkAdapter(mock);
            int afterSet = raised;
            mock.SetRewardedReady("bonus", true);

            Assert.That(afterSet, Is.GreaterThan(0), "setting and initializing an adapter changes readiness");
            Assert.That(raised, Is.GreaterThan(afterSet), "the adapter's own readiness changes are forwarded");
        }

        [Test]
        public async Task PrivacyOptions_DegradeWithoutConsentCapability()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            ads.SetNetworkAdapter(new BoolOnlyAdAdapter());
            bool closed = false;

            ads.ShowPrivacyOptions(() => closed = true);

            Assert.That(ads.PrivacyOptionsRequired, Is.False);
            Assert.That(closed, Is.True, "the callback completes immediately when there is nothing to show");

            var mock = new MockAdNetworkAdapter { PrivacyOptionsRequired = true };
            ads.SetNetworkAdapter(mock);
            ads.ShowPrivacyOptions();
            Assert.That(ads.PrivacyOptionsRequired, Is.True);
            Assert.That(mock.PrivacyOptionsShown, Is.EqualTo(1));
        }

        [Test]
        public async Task LocalizedPrice_PrefersTheStoreOverTheCatalog()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.RegisterProducts(new ProductDefinition { Id = "pass", PriceString = "$1.99" }, new ProductDefinition { Id = "coins", PriceString = "$0.99" });

            Assert.That(iap.GetLocalizedPrice("pass"), Is.EqualTo("$1.99"), "catalog price without a store");
            iap.SetStoreAdapter(new PricedStoreAdapter());
            Assert.That(iap.GetLocalizedPrice("pass"), Is.EqualTo("19,99 TL"), "the store's localized price wins");
            Assert.That(iap.GetLocalizedPrice("coins"), Is.EqualTo("$0.99"), "catalog price when the store has no answer");
            Assert.That(iap.GetLocalizedPrice("unknown"), Is.Null);
        }

        [Test]
        public async Task PurchaseOutcome_MapsBoolOnlyAdaptersAndUsesOutcomeAdapters()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            iap.RegisterProducts(new ProductDefinition { Id = "pass", PriceString = "$1.99" });

            iap.SetStoreAdapter(new PricedStoreAdapter());
            PurchaseOutcome? outcome = null;
            iap.Purchase("pass", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(PurchaseOutcome.Failed), "a bool adapter's refusal is a failure");

            iap.SetStoreAdapter(new MockIapStoreAdapter());
            iap.Purchase("missing", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(PurchaseOutcome.Failed), "unknown product");
        }

        [Test]
        public async Task StoreChanged_FollowsTheAdapter()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            int raised = 0;
            iap.StoreChanged += () => raised++;
            var store = new PricedStoreAdapter();

            iap.SetStoreAdapter(store);
            store.RaiseChanged();

            Assert.That(raised, Is.EqualTo(1));
            Assert.That(iap.IsStoreAvailable, Is.True);
        }
    }
}
