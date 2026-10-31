using System.Collections;
using NUnit.Framework;
using Nexus.Ads.Unity;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Ads.Bridge.Tests.Runtime
{
    /// <summary>The Nexus sample's path: AdService over the bridge over the editor's simulator bootstrap.</summary>
    public sealed class NexusAdsBridgePlayModeTests
    {
        private GameObject _root;
        private AdsSettings _settings;
        private AdService _service;

        [TearDown]
        public void TearDown()
        {
            _service?.Dispose();
            if (_root != null) Object.Destroy(_root);
            if (_settings != null) Object.Destroy(_settings);
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        [UnityTest]
        public IEnumerator ConnectBeforeTheBootstrapStarted_ThenARewardFlowsThroughTheService()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _root = new GameObject("BridgeTest");
            _root.SetActive(false);
            var bootstrap = _root.AddComponent<AdsBootstrap>();
            bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false);
            _root.SetActive(true);

            _service = new AdService();
            var factory = new AdAdapterFactory();
            NexusAdsBridge.Connect(_service, factory, bootstrap: bootstrap); // the bootstrap has not fetched the device id yet
            Assert.IsNull(bootstrap.Controller, "the connection is deferred to Started");
            Assert.IsNull(factory.CreateAdapter("simulator"), "the provider name is registered only once the controller exists");

            float deadline = Time.realtimeSinceStartup + 5f;
            while (bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(bootstrap.Controller);
            Assert.IsInstanceOf<NexusAdsAdapter>(factory.CreateAdapter("simulator"), "connected on Started");
            yield return null;
            Assert.IsTrue(_service.IsRewardedAvailable("shop_refill"), "the simulator filled");

            AdOutcome? outcome = null;
            _service.RequestReward("shop_refill", o => outcome = o);
            Assert.IsNull(outcome, "the overlay decides");
            Assert.AreEqual(0f, Time.timeScale);
            bootstrap.Simulator.CompleteShow(AdResult.Rewarded);
            Assert.AreEqual(AdOutcome.Rewarded, outcome);
            Assert.AreEqual(1f, Time.timeScale);
            Assert.IsTrue(bootstrap.Controller.PendingReward.HasValue, "the receipt waits for the game's acknowledge");
        }

        // Issue #165: a host that runs no ads kept a simulator, and the bridge's Initialize started it, so a release game paid every reward
        [UnityTest]
        public IEnumerator ReleaseHostThatRunsNoAds_StaysStoppedThroughTheBridge_AndPaysNoReward()
        {
            var previous = AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, (scheduler, _) => new SimulatorProvider(scheduler));
            AdsBootstrap.BuildForTests = BuildKind.Release;
            AdsBootstrap.RegisterSharedConsentFlow(null, AdProviderKind.None);
            try
            {
                _settings = ScriptableObject.CreateInstance<AdsSettings>();
                _settings.SelectedMediation = AdProviderKind.LevelPlay;
                _settings.Consent = ConsentSource.GameFlow; // and no flow registered: the bootstrap starts no ads (issue #154)
                _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
                _settings.Release.LevelPlay.Android.AppKey = "live";
                _settings.Release.LevelPlay.Android.RewardedUnitId = "live-rewarded";
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] no ads run: the settings name the game's own consent flow before LevelPlay"));
                _root = new GameObject("BridgeTest");
                _root.SetActive(false);
                var bootstrap = _root.AddComponent<AdsBootstrap>();
                bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false);
                _root.SetActive(true);

                _service = new AdService();
                NexusAdsBridge.Connect(_service, new AdAdapterFactory(), bootstrap: bootstrap);
                float deadline = Time.realtimeSinceStartup + 5f;
                while (bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsNotNull(bootstrap.Controller, "the bootstrap started within 5 s");
                Assert.AreEqual(HostMode.Idle, bootstrap.Mode);
                var leaked = bootstrap.Controller.PendingReward; // the receipt store is PlayerPrefs, shared with the test above
                if (leaked.HasValue) bootstrap.Controller.AcknowledgeReward(leaked.Value.Id);
                for (int i = 0; i < 5; i++) yield return null; // the old simulator filled within a frame

                Assert.AreEqual(AdController.Phase.Stopped, bootstrap.Controller.State, "the bridge's Initialize does not start a host that runs no ads");
                Assert.IsTrue(_service.IsInitialized, "the service is told there is nothing to wait for");
                Assert.IsFalse(_service.IsRewardedAvailable("shop_refill"));
                AdOutcome? outcome = null;
                _service.RequestReward("shop_refill", o => outcome = o);
                Assert.AreEqual(AdOutcome.NotReady, outcome);
                Assert.AreEqual(1f, Time.timeScale, "nothing paused the game");
                Assert.IsFalse(bootstrap.Controller.PendingReward.HasValue, "no receipt was written");
            }
            finally
            {
                AdsBootstrap.RegisterSharedConsentFlow(null, AdProviderKind.None);
                AdsBootstrap.BuildForTests = null;
                AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, previous);
            }
        }
    }
}
