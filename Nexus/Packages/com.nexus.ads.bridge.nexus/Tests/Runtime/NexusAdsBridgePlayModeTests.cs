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
    }
}
