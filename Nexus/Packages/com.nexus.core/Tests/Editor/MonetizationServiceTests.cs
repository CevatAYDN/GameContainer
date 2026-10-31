using NUnit.Framework;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    }
}
