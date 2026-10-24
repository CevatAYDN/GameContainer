#if NEXUS_LEVELPLAY
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Nexus.Ads.Unity;
using Unity.Services.LevelPlay;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Ads.LevelPlay.Tests.Runtime
{
    /// <summary>
    /// The provider over the LevelPlay package's own editor platform (present on the Android and iOS
    /// build targets only): Init answers at once, loads are mocked, a show instantiates a mock ad prefab
    /// whose close fires OnAdClosed and then OnAdRewarded, the late-reward order the provider's grace
    /// period exists for. The prefab closes from its IMGUI countdown, which batch mode never draws, so
    /// the test closes it through the prefab's own HideAd (reflection on the package's internal type).
    /// </summary>
    public sealed class LevelPlayProviderPlayModeTests
    {
        private GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.Destroy(_host);
            // each ad object leaves its mock prefab in the scene; a later Close would otherwise find a stale one
            foreach (var name in new[] { "RewardedPrefab", "InterstitialPrefab" })
                foreach (var prefab in Object.FindObjectsByType(PrefabType(name), FindObjectsSortMode.None))
                    Object.DestroyImmediate(((MonoBehaviour)prefab).gameObject);
        }

        private static System.Type PrefabType(string name) => typeof(LevelPlayRewardedAd).Assembly.GetTypes().First(t => t.Name == name);

        private static ProviderConfig Config() => new ProviderConfig("key",
            new Dictionary<AdFormat, string> { { AdFormat.Rewarded, "rewarded" }, { AdFormat.Interstitial, "interstitial" } }, true, new[] { "gaid" }, false, false, false, "G");

        [Test]
        public void TheBootstrapKnowsTheProvider_UnderItsKind()
        {
            Assert.IsTrue(AdsBootstrap.IsProviderRegistered(AdProviderKind.LevelPlay), "registered by RuntimeInitializeOnLoadMethod before the first scene");
            Assert.AreEqual("levelplay", LevelPlayProvider.ProviderName);
        }

        [Test]
        public void Initialize_WithTheEditorSdk_CreatesTheAdUnits_AndLoadsThem()
        {
            _host = new GameObject("scheduler");
            var provider = new LevelPlayProvider(_host.AddComponent<UnityAdScheduler>());
            bool? ready = null;
            var loaded = new List<AdFormat>();
            provider.Loaded += f => loaded.Add(f);
            provider.Initialize(Config(), r => ready = r);
            Assert.AreEqual(true, ready, "the editor SDK initializes synchronously on the Android build target");
            provider.Load(AdFormat.Rewarded);
            provider.Load(AdFormat.Interstitial);
            CollectionAssert.AreEquivalent(new[] { AdFormat.Rewarded, AdFormat.Interstitial }, loaded);
            Assert.IsTrue(provider.IsReady(AdFormat.Rewarded) && provider.IsReady(AdFormat.Interstitial));
        }

        [UnityTest]
        public IEnumerator ARewardThatArrivesAfterClose_StillPays_Once()
        {
            _host = new GameObject("scheduler");
            var provider = new LevelPlayProvider(_host.AddComponent<UnityAdScheduler>());
            provider.Initialize(Config(), _ => { });
            provider.Load(AdFormat.Rewarded);
            Assert.IsTrue(provider.IsReady(AdFormat.Rewarded));

            var results = new List<AdResult>();
            int opened = 0;
            provider.Opened += _ => opened++;
            provider.Show(AdFormat.Rewarded, "shop_refill", results.Add);
            Assert.AreEqual(1, opened, "OnAdDisplayed reached Opened");
            yield return null;

            // the mock prefab's IMGUI countdown never runs in batch mode: close it the way its countdown would
            Close("RewardedPrefab");
            Assert.AreEqual(1, results.Count, "exactly one answer");
            Assert.AreEqual(AdResult.Rewarded, results[0], "the mock closes, then rewards; the late reward is caught");
            yield return new WaitForSecondsRealtime((float)(LevelPlayProvider.RewardGraceSeconds * 1.5));
            Assert.AreEqual(1, results.Count, "no second answer after the grace period");
        }

        [UnityTest]
        public IEnumerator AnInterstitialClose_AnswersClosed_AtOnce()
        {
            _host = new GameObject("scheduler");
            var provider = new LevelPlayProvider(_host.AddComponent<UnityAdScheduler>());
            provider.Initialize(Config(), _ => { });
            provider.Load(AdFormat.Interstitial);
            var results = new List<AdResult>();
            provider.Show(AdFormat.Interstitial, "level_end", results.Add);
            yield return null;
            Close("InterstitialPrefab");
            Assert.AreEqual(1, results.Count, "an interstitial close answers at once, no grace period");
            Assert.AreEqual(AdResult.Closed, results[0]);
        }

        [UnityTest]
        public IEnumerator Revenue_CarriesTheFormatOfTheAdThatEarnedIt_WhateverFormatStringTheSdkSends()
        {
            _host = new GameObject("scheduler");
            var provider = new LevelPlayProvider(_host.AddComponent<UnityAdScheduler>());
            var revenue = new List<AdFormat>();
            provider.Revenue += r => revenue.Add(r.Format);
            provider.Initialize(Config(), _ => { });
            provider.Load(AdFormat.Rewarded);
            provider.Show(AdFormat.Rewarded, "shop_refill", _ => { });
            yield return null;
            Close("RewardedPrefab");
            provider.Load(AdFormat.Interstitial);
            provider.Show(AdFormat.Interstitial, "level_end", _ => { });
            yield return null;
            Close("InterstitialPrefab");
            // the editor SDK sends "editor_mock_format" and the Android SDK "rewarded_video"; neither names the format the old parser knew
            CollectionAssert.AreEqual(new[] { AdFormat.Rewarded, AdFormat.Interstitial }, revenue);
        }

        private static void Close(string prefabTypeName)
        {
            var prefabType = PrefabType(prefabTypeName);
            var prefab = Object.FindAnyObjectByType(prefabType) as MonoBehaviour;
            Assert.IsNotNull(prefab, "the editor platform instantiated its mock ad " + prefabTypeName);
            prefabType.GetMethod("HideAd", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(prefab, null);
        }
    }
}
#endif
