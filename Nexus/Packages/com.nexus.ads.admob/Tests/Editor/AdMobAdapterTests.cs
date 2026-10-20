#if NEXUS_ADMOB
using Nexus.Core.Services;
using NUnit.Framework;

namespace Nexus.Ads.AdMob.Tests
{
    /// <summary>Before the SDK started nothing may be shown and every request reports NotReady; the registration name is stable.</summary>
    public sealed class AdMobAdapterTests
    {
        [Test]
        public void BeforeInitialize_NothingIsReady_AndRequestReportsNotReady()
        {
            var adapter = new AdMobAdapter(new AdMobConfig { RewardedAdUnitIdAndroid = "unit" });
            Assert.IsFalse(adapter.IsRewardedReady("any"));
            Assert.IsFalse(adapter.IsInterstitialReady("any"));
            Assert.IsFalse(adapter.PrivacyOptionsRequired);
            AdOutcome? outcome = null;
            adapter.RequestReward("any", o => outcome = o);
            Assert.AreEqual(AdOutcome.NotReady, outcome);
            bool closed = false;
            adapter.ShowInterstitial("any", () => closed = true);
            Assert.IsTrue(closed, "an interstitial that cannot be shown completes immediately");
        }

        [Test]
        public void ConsentDebugSettings_OnlyWhenEeaOrATestDeviceIsSet()
        {
            Assert.IsFalse(new AdMobConfig().WantsConsentDebug, "an untouched config sends the consent request as before");
            Assert.IsFalse(new AdMobConfig { TestDeviceHashedIds = new System.Collections.Generic.List<string>() }.WantsConsentDebug);
            Assert.IsTrue(new AdMobConfig { DebugGeographyEea = true }.WantsConsentDebug);
            Assert.IsTrue(new AdMobConfig { TestDeviceHashedIds = new System.Collections.Generic.List<string> { "HASH" } }.WantsConsentDebug);
        }

        [Test]
        public void Register_UsesTheProviderName()
        {
            var factory = new AdAdapterFactory();
            AdMobAdapter.Register(factory, new AdMobConfig());
            Assert.IsInstanceOf<AdMobAdapter>(factory.CreateAdapter(AdMobAdapter.ProviderName));
        }
    }
}
#endif
