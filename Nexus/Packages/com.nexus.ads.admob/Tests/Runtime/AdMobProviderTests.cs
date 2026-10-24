using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Nexus.Ads.AdMob.Tests
{
    /// <summary>
    /// The provider over the Google Mobile Ads plugin's own editor platform: consent answers from the
    /// plugin's UMP placeholder, the SDK starts through the plugin's main-thread queue (so these run in
    /// Play Mode), Google's sample units load placeholder ads, and a show instantiates a placeholder whose
    /// countdown pays the reward before its close button appears.
    /// </summary>
    public sealed class AdMobProviderTests
    {
        private const string SampleRewarded = "ca-app-pub-3940256099942544/5224354917";
        private const string SampleInterstitial = "ca-app-pub-3940256099942544/1033173712";
        private const float TimeoutSeconds = 20f;

        [SetUp]
        public void SetUp() => ConsentInformation.Reset();

        [TearDown]
        public void TearDown() => AdMobProvider.SetTestDevices(null);

        private static ProviderConfig Config(bool sampleUnits = true, bool childDirected = false) => new ProviderConfig(
            "ca-app-pub-3940256099942544~3347511713",
            new Dictionary<AdFormat, string> { { AdFormat.Rewarded, SampleRewarded }, { AdFormat.Interstitial, SampleInterstitial } },
            false, null, sampleUnits, childDirected, false, "PG");

        private static IEnumerator Until(Func<bool> condition, string what)
        {
            float end = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }

        private static IEnumerator Started(AdMobProvider provider, ProviderConfig config)
        {
            bool consented = false;
            provider.Request(() => consented = true);
            yield return Until(() => consented, "the consent answer");
            bool? ready = null;
            provider.Initialize(config, r => ready = r);
            yield return Until(() => ready.HasValue, "MobileAds.Initialize");
            Assert.AreEqual(true, ready);
        }

        [Test]
        public void TheBootstrapKnowsTheProvider_UnderItsKind()
        {
            Assert.IsTrue(AdsBootstrap.IsProviderRegistered(AdProviderKind.AdMob), "registered by RuntimeInitializeOnLoadMethod before the first scene");
        }

        [Test]
        public void BeforeInitialization_LoadFailsWithTheReason_AndAShowAnswersNotReady()
        {
            var provider = new AdMobProvider(Config());
            string reason = null;
            provider.LoadFailed += (_, r) => reason = r;
            provider.Load(AdFormat.Rewarded);
            Assert.AreEqual("SDK not initialized", reason);
            AdResult? result = null;
            provider.Show(AdFormat.Rewarded, "shop_refill", r => result = r);
            Assert.AreEqual(AdResult.NotReady, result);
            Assert.IsFalse(provider.Completed, "consent is asked on every launch, never assumed");
        }

        [UnityTest]
        public IEnumerator Initialize_SendsChildAgeTreatmentAndRating_ToTheRequestConfiguration()
        {
            yield return Started(new AdMobProvider(Config(childDirected: true)), Config(childDirected: true));
            var requestConfiguration = MobileAds.GetRequestConfiguration();
            Assert.AreEqual(AgeRestrictedTreatment.Child, requestConfiguration.AgeRestrictedTreatment, "the old adapter never sent child-directed treatment");
            Assert.AreEqual(MaxAdContentRating.PG.Value, requestConfiguration.MaxAdContentRating.Value);
        }

        [UnityTest]
        public IEnumerator GoogleTestDevices_ReachTheSdk_OnlyWithGoogleSampleUnits()
        {
            AdMobProvider.SetTestDevices(new[] { "33BE2250B43518CCDA7DE426D04EE231" });
            yield return Started(new AdMobProvider(Config(sampleUnits: false)), Config(sampleUnits: false));
            CollectionAssert.IsEmpty(MobileAds.GetRequestConfiguration().TestDeviceIds, "live units: no test configuration");
            var provider = new AdMobProvider(Config());
            provider.Initialize(Config(), _ => { });
            CollectionAssert.AreEqual(new[] { "33BE2250B43518CCDA7DE426D04EE231" }, MobileAds.GetRequestConfiguration().TestDeviceIds);
        }

        [UnityTest]
        public IEnumerator ARewardedSampleAd_LoadsShowsAndPays_Once()
        {
            var provider = new AdMobProvider(Config());
            yield return Started(provider, Config());
            bool loaded = false;
            provider.Loaded += f => loaded |= f == AdFormat.Rewarded;
            provider.LoadFailed += (f, r) => Assert.Fail("load failed: " + r);
            provider.Load(AdFormat.Rewarded);
            yield return Until(() => loaded, "the placeholder rewarded ad");
            Assert.IsTrue(provider.IsReady(AdFormat.Rewarded));

            int opened = 0;
            provider.Opened += _ => opened++;
            var results = new List<AdResult>();
            provider.Show(AdFormat.Rewarded, "shop_refill", results.Add);
            yield return Until(() => opened == 1, "OnAdFullScreenContentOpened");
            yield return CloseThePlaceholder();
            yield return Until(() => results.Count > 0, "the show result");
            yield return null;
            CollectionAssert.AreEqual(new[] { AdResult.Rewarded }, results, "the countdown paid, then the close answered once");
            Assert.IsFalse(provider.IsReady(AdFormat.Rewarded), "a shown ad is spent; the controller loads the next one");
        }

        [UnityTest]
        public IEnumerator AnInterstitialSampleAd_AnswersClosed()
        {
            var provider = new AdMobProvider(Config());
            yield return Started(provider, Config());
            bool loaded = false;
            provider.Loaded += f => loaded |= f == AdFormat.Interstitial;
            provider.Load(AdFormat.Interstitial);
            yield return Until(() => loaded, "the placeholder interstitial");
            var results = new List<AdResult>();
            provider.Show(AdFormat.Interstitial, "level_end", results.Add);
            yield return CloseThePlaceholder();
            yield return Until(() => results.Count > 0, "the show result");
            CollectionAssert.AreEqual(new[] { AdResult.Closed }, results);
        }

        /// <summary>Presses the placeholder's "Close Ad" button once it is clickable (the rewarded one after its 5 s countdown).</summary>
        private static IEnumerator CloseThePlaceholder()
        {
            // the close button sits inside the clickable ad: walk up from its label, not down from a button
            Button close = null;
            yield return Until(() => (close = UnityEngine.Object.FindObjectsByType<Text>()
                .Where(t => t.text == "Close Ad").Select(t => t.GetComponentInParent<Button>()).FirstOrDefault(b => b != null && b.interactable)) != null, "the placeholder's close button");
            close.onClick.Invoke();
        }
    }
}
