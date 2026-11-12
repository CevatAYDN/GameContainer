#if NEXUS_ADS
using System.Collections;
using NUnit.Framework;
using Nexus.Ads;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Capture.Tests
{
    /// <summary>
    /// PlayMode tests locking ad suppression behavior during Capture Mode.
    /// </summary>
    public sealed class CaptureAdSuppressionTests
    {
        private GameObject _root;
        private AdsSettings _settings;

        [TearDown]
        public void Teardown()
        {
            CaptureModeService.Close();

            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
                _settings = null;
            }
        }

        [UnityTest]
        public IEnumerator Ads_ControllerStartedAfterOpen_IsSuppressed_AndLiftedOnClose()
        {
            CaptureModeService.Open();
            Assert.That(CaptureModeService.IsOpen, Is.True, "Capture mode must be open.");

            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "test_placement", Format = AdFormat.Rewarded });

            _root = new GameObject("AdsBootstrapCaptureTest");
            _root.SetActive(false);
            var bootstrap = _root.AddComponent<AdsBootstrap>();
            bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false);
            _root.SetActive(true);

            float deadline = Time.realtimeSinceStartup + 5f;
            while (bootstrap.Controller == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(bootstrap.Controller, Is.Not.Null, "The bootstrap started within 5 s.");
            CaptureAdSuppression.Tick();

            Assert.That(bootstrap.Controller.Suppressed, Is.True, "Controller must be suppressed while Capture Mode is open.");

            CaptureModeService.Close();

            Assert.That(bootstrap.Controller.Suppressed, Is.False, "Suppression must be lifted on Close.");
        }
    }
}
#endif
