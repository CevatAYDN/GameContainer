using System.Collections;
using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Nexus.Ads.Tests.Runtime
{
    /// <summary>The Unity layer around the core: the simulator path the editor runs, the pause, the scheduler and the button.</summary>
    public sealed class AdsBootstrapPlayModeTests
    {
        private GameObject _root;
        private AdsSettings _settings;
        private AdsBootstrap _bootstrap;

        private IEnumerator Boot(SimulatorScenario scenario, bool logEvents = false)
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.Placements.Add(new PlacementEntry { Name = "level_end", Format = AdFormat.Interstitial, NaturalBreak = true });
            _root = new GameObject("AdsBootstrapTest");
            _root.SetActive(false);
            _bootstrap = _root.AddComponent<AdsBootstrap>();
            _bootstrap.Configure(_settings, scenario, logEvents);
            _root.SetActive(true);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(_bootstrap.Controller, "the bootstrap started within 5 s");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.Destroy(_root);
            if (_settings != null) Object.Destroy(_settings);
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        [UnityTest]
        public IEnumerator StartOnAwakeOff_TouchesNothingUntilStartAds_ThenStartsOnce()
        {
            // issue #62: a game that starts ads late (Gigawatt at the first offer, Wickling after the first night) needs a bootstrap that waits
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _root = new GameObject("AdsBootstrapTest");
            _root.SetActive(false);
            _bootstrap = _root.AddComponent<AdsBootstrap>();
            _bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false, startOnAwake: false);
            int started = 0, readyChanges = 0;
            _bootstrap.Started += () => started++;
            _root.SetActive(true);
            var unsubscribe = Nexus.Ads.Unity.Ads.OnReadyChanged(() => readyChanges++); // made before the start, attached by it

            for (int i = 0; i < 10; i++) yield return null;
            Assert.IsNull(_bootstrap.Controller, "no controller before StartAds");
            Assert.IsNull(_bootstrap.DeviceAdvertisingId, "the advertising id is not read before StartAds");
            Assert.AreEqual(0, started);
            Assert.AreEqual(AdResult.NotReady, ShowNow(), "a show before the start answers NotReady");

            _bootstrap.StartAds();
            _bootstrap.StartAds();
            float deadline = Time.realtimeSinceStartup + 5f;
            while ((_bootstrap.Controller == null || readyChanges == 0) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(_bootstrap.Controller, "StartAds started the bootstrap within 5 s");
            Assert.AreEqual(1, started, "a second StartAds starts nothing");
            Assert.Greater(readyChanges, 0, "the early ReadyChanged subscription was attached");
            Assert.Throws<System.InvalidOperationException>(() => _bootstrap.Configure(_settings), "Configure after the start is refused");
            unsubscribe();
        }

        // issue #133: StartAds read the advertising id before every start; in the editor the gate never compares it
        [UnityTest]
        public IEnumerator Start_InTheEditor_ReadsNoAdvertisingId()
        {
            int reads = 0;
            var reader = AdsBootstrap.AdvertisingIdReader;
            AdsBootstrap.AdvertisingIdReader = done => { reads++; done("fake-id"); };
            try { yield return Boot(SimulatorScenario.Normal); }
            finally { AdsBootstrap.AdvertisingIdReader = reader; }

            Assert.That((reads, _bootstrap.DeviceAdvertisingId), Is.EqualTo((0, (string)null)));
        }

        private static AdResult ShowNow()
        {
            AdResult result = AdResult.Failed;
            Nexus.Ads.Unity.Ads.Show("shop_refill", r => result = r);
            return result;
        }

        [UnityTest]
        public IEnumerator Editor_RunsTheSimulator_AndAShowPausesTimeAndAudioUntilItCloses()
        {
            yield return Boot(SimulatorScenario.Normal);
            Assert.AreEqual(GateDecision.Simulator, _bootstrap.Gate.Decision);
            Assert.IsNotNull(_bootstrap.Simulator);
            Assert.IsTrue(_bootstrap.Controller.IsReady("shop_refill"), "the simulator fills at once");

            AdResult? result = null;
            _bootstrap.Controller.Show("shop_refill", r => result = r);
            Assert.IsTrue(_bootstrap.Simulator.IsShowing);
            Assert.AreEqual(0f, Time.timeScale);
            Assert.IsTrue(AudioListener.pause);
            Assert.IsNull(result, "the overlay decides");

            _bootstrap.Simulator.CompleteShow(AdResult.Rewarded);
            Assert.AreEqual(AdResult.Rewarded, result);
            Assert.AreEqual(1f, Time.timeScale);
            Assert.IsFalse(AudioListener.pause);
            yield return null;
            Assert.IsTrue(_bootstrap.Controller.IsReady("shop_refill"), "reloaded after the show");
        }

        [UnityTest]
        public IEnumerator Interstitial_InTheFirstMinute_IsBlocked_WithAViolation()
        {
            yield return Boot(SimulatorScenario.Normal);
            PolicyViolation? seen = null;
            _bootstrap.Controller.Violation += v => seen = v;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("InterstitialFrequency"));
            AdResult? result = null;
            _bootstrap.Controller.Show("level_end", r => result = r);
            Assert.AreEqual(AdResult.Blocked, result);
            Assert.AreEqual(PolicyRules.InterstitialFrequency, seen?.Rule);
            Assert.AreEqual(1f, Time.timeScale, "nothing opened, nothing paused");
        }

        // Issue #36: with event logging on, the telemetry log and the bootstrap's Violation handler both wrote the
        // block as an error; a second error line fails this test as an unexpected log.
        // Issue #48: the one line keeps the text tools/device-acceptance/run.ps1 counts as a policy violation.
        [UnityTest]
        public IEnumerator BlockedShow_IsLoggedOnce_WithTheTokenTheAcceptanceScanCounts()
        {
            yield return Boot(SimulatorScenario.Normal, logEvents: true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] PolicyViolation InterstitialFrequency"));
            AdResult? result = null;
            _bootstrap.Controller.Show("level_end", r => result = r);
            Assert.AreEqual(AdResult.Blocked, result);
        }

        // Issue #52: the real SDK's start logged nothing, so logcat could not tell it from a silent failure;
        // tools/device-acceptance/run.ps1 -ExpectAds reads this line.
        [UnityTest]
        public IEnumerator Start_LogsOneLine_WithTheModeTheGateAndTestMode()
        {
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] started mode=Simulator mediation=LevelPlay gate=Simulator rule=- testMode=(on|off)$"));
            yield return Boot(SimulatorScenario.Normal);
        }

        [UnityTest]
        public IEnumerator Scheduler_FiresOnUnscaledTime_AndACancelledDelayNeverFires()
        {
            var go = new GameObject("scheduler");
            try
            {
                var scheduler = go.AddComponent<UnityAdScheduler>();
                int fired = 0, cancelled = 0;
                Time.timeScale = 0f;
                scheduler.Schedule(0.1, () => fired++);
                var handle = scheduler.Schedule(0.1, () => cancelled++);
                handle.Dispose();
                yield return new WaitForSecondsRealtime(0.3f);
                Assert.AreEqual(1, fired, "fires with time scale 0");
                Assert.AreEqual(0, cancelled);
            }
            finally
            {
                Time.timeScale = 1f;
                Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator OnGUI_RunsOnlyWhileThePanelIsOpenOrAShowIsOpen()
        {
            // issue #83: an enabled OnGUI allocates every frame even when it returns at once (61,824 B in 168 frames on a phone)
            yield return Boot(SimulatorScenario.Normal);
            var panel = _root.GetComponent<AdsDebugPanel>();
            Assert.IsNotNull(panel, "the editor is not a release build, so the panel is there");
            Assert.IsNull(EnabledOnGui(), "closed panel, no ad: nothing draws");

            _bootstrap.Controller.Show("shop_refill", _ => { });
            Assert.IsNotNull(EnabledOnGui(), "the overlay draws while the show is open");
            _bootstrap.Simulator.CompleteShow(AdResult.Closed);
            yield return null;
            Assert.IsNull(EnabledOnGui(), "the overlay stops when the show closes");

            panel.Toggle();
            Assert.IsNotNull(EnabledOnGui(), "the open panel draws");
            panel.Toggle();
            Assert.IsNull(EnabledOnGui(), "the closed panel stops drawing");
        }

        private string EnabledOnGui()
        {
            foreach (var behaviour in _root.GetComponents<MonoBehaviour>())
            {
                var onGui = behaviour.GetType().GetMethod("OnGUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (behaviour.enabled && onGui != null) return behaviour.GetType().Name;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator RewardedButton_IsHiddenWhileNothingIsLoaded_AndPaysOnceOnReward()
        {
            yield return Boot(SimulatorScenario.NoFill);
            var buttonObject = new GameObject("button", typeof(RectTransform), typeof(CanvasGroup), typeof(Button));
            buttonObject.transform.SetParent(_root.transform);
            var button = buttonObject.AddComponent<AdRewardedButton>();
            int rewarded = 0;
            button.OnRewarded.AddListener(() => rewarded++);
            button.Placement = "shop_refill";
            yield return null;
            var group = buttonObject.GetComponent<CanvasGroup>();
            Assert.AreEqual(0f, group.alpha, "no fill: the button hides itself");

            _bootstrap.Simulator.Scenario = SimulatorScenario.Normal;
            _bootstrap.Simulator.Load(AdFormat.Rewarded); // the retry would do this after the back-off; the test skips the wait
            yield return null;
            Assert.AreEqual(1f, group.alpha, "loaded: visible again by itself");

            buttonObject.GetComponent<Button>().onClick.Invoke();
            Assert.AreEqual(0f, group.alpha, "hidden while the ad is open");
            _bootstrap.Simulator.CompleteShow(AdResult.Rewarded);
            _bootstrap.Simulator.CompleteShow(AdResult.Rewarded);
            Assert.AreEqual(1, rewarded);
        }
    }
}
