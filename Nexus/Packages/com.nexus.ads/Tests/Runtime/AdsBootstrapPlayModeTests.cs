using System.Collections;
using System.Collections.Generic;
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

        private IEnumerator Boot(SimulatorScenario scenario, bool logEvents = false, double touchWaitSeconds = 0, bool simulatorOverlay = true, bool debugPanel = true)
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Policy.NoInterstitialAfterTouchSeconds = touchWaitSeconds;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.Placements.Add(new PlacementEntry { Name = "level_end", Format = AdFormat.Interstitial, NaturalBreak = true });
            _root = new GameObject("AdsBootstrapTest");
            _root.SetActive(false);
            _bootstrap = _root.AddComponent<AdsBootstrap>();
            _bootstrap.Configure(_settings, scenario, logEvents, simulatorOverlay: simulatorOverlay, debugPanel: debugPanel);
            _root.SetActive(true);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(_bootstrap.Controller, "the bootstrap started within 5 s");
        }

        // RewardReceipts' row in PlayerPrefs: a test that pays a reward leaves its receipt, so each test starts and ends without one
        private const string ReceiptRow = "nexus.ads.reward.pending";

        [SetUp]
        public void SetUp() => PlayerPrefs.DeleteKey(ReceiptRow);

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(ReceiptRow);
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

        // Issue #174: every close restored the time scale and audio saved at the last open, also after a show that never opened, so
        // a failed show from a paused game's menu resumed the game behind the menu, with sound
        [UnityTest]
        public IEnumerator AShowThatNeverOpened_LeavesTheGamesPausedTimeAndAudioAsTheyWere()
        {
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] started mode=Simulator "));
            yield return Boot(SimulatorScenario.ShowFails);
            Time.timeScale = 0f;
            AudioListener.pause = true;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] ShowFailed Rewarded 'shop_refill'"));
            Assert.AreEqual(AdResult.Failed, ShowNow());
            Assert.AreEqual(0f, Time.timeScale, "the game's pause holds");
            Assert.IsTrue(AudioListener.pause, "the game's muted audio holds");
            LogAssert.NoUnexpectedReceived();
        }

        // Issue #178: Ads.PendingReward read the controller, which does not exist before StartAds (startOnAwake off) or until the
        // posted start ran, so the documented start-up check found nothing and the next reward overwrote the receipt
        [UnityTest]
        public IEnumerator APendingReward_IsReadAndAcknowledged_BeforeTheAdsStart()
        {
            PlayerPrefs.SetString(ReceiptRow, "r-1|shop_refill");
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _root = new GameObject("AdsBootstrapTest");
            _root.SetActive(false);
            _bootstrap = _root.AddComponent<AdsBootstrap>();
            _bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false, startOnAwake: false);
            _root.SetActive(true);
            yield return null;

            Assert.IsNull(_bootstrap.Controller, "the ads have not started");
            var receipt = Nexus.Ads.Unity.Ads.PendingReward;
            Assert.IsTrue(receipt.HasValue, "the receipt of the killed run is read before the start");
            Assert.AreEqual("r-1", receipt.Value.Id);
            Assert.AreEqual("shop_refill", receipt.Value.Placement);
            Nexus.Ads.Unity.Ads.AcknowledgeReward("r-1");
            Assert.IsFalse(Nexus.Ads.Unity.Ads.PendingReward.HasValue, "acknowledged before the start");
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

        // Issue #126: the bootstrap wires its UnityTouchInput into the guard, so the settings' touch wait blocks an
        // interstitial that would open under the player's finger.
        [UnityTest]
        public IEnumerator Interstitial_RightAfterATouch_IsBlocked_AsInterstitialAfterTouch()
        {
            yield return Boot(SimulatorScenario.Normal, touchWaitSeconds: 5);
            var touch = _root.GetComponent<UnityTouchInput>();
            touch.enabled = false; // only Touched() below feeds the guard, not the machine's mouse
            touch.Touched();
            PolicyViolation? seen = null;
            _bootstrap.Controller.Violation += v => seen = v;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("InterstitialAfterTouch"));
            AdResult? result = null;
            _bootstrap.Controller.Show("level_end", r => result = r);
            Assert.AreEqual(AdResult.Blocked, result);
            Assert.AreEqual(PolicyRules.InterstitialAfterTouch, seen?.Rule);
        }

        // Issue #126: the touch and the guard read one timeline (UnityAdClock's), so the wait ends on time; the first-minute
        // floor behind it is what blocks now. The interstitial allowed once both have ended is the harness's PG32.
        [UnityTest]
        public IEnumerator Interstitial_AfterTheTouchWait_IsNoLongerHeldBackByTheTouch()
        {
            yield return Boot(SimulatorScenario.Normal, touchWaitSeconds: 0.2);
            var touch = _root.GetComponent<UnityTouchInput>();
            touch.enabled = false; // only Touched() below feeds the guard, not the machine's mouse
            touch.Touched();
            yield return new WaitForSecondsRealtime(0.3f);
            PolicyViolation? seen = null;
            _bootstrap.Controller.Violation += v => seen = v;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("InterstitialFrequency"));
            AdResult? result = null;
            _bootstrap.Controller.Show("level_end", r => result = r);
            Assert.AreEqual(AdResult.Blocked, result);
            Assert.AreEqual(PolicyRules.InterstitialFrequency, seen?.Rule, "the touch wait has ended; the first minute holds the interstitial");
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
        public IEnumerator Start_LogsOneLine_WithTheModeTheGateTestModeAndConsentFlow()
        {
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] started mode=Simulator mediation=LevelPlay gate=Simulator rule=- testMode=(on|off) consent=SimulatorProvider\(simulator\)$"));
            yield return Boot(SimulatorScenario.Normal);
        }

        // Issue #154: LevelPlay ships no consent form; a release build started it with only its pass-through
        [UnityTest]
        public IEnumerator ReleaseLevelPlay_StartsNoAds_UntilTheConsentFlowTheSettingsNameIsRegistered()
        {
            var previous = AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, (scheduler, _) => new SimulatorProvider(scheduler));
            AdsBootstrap.BuildForTests = BuildKind.Release;
            var previousConsent = AdsBootstrap.SwapConsentFlowsForTests(_ => new SimulatorProvider(new NeverScheduler()), null); // Google's form only (issue #181)
            try
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] no ads run: the settings name the game's own consent flow before LevelPlay, and none is registered: .*; Google's form from com.nexus.ads.admob runs only with Consent: GoogleUmp$"));
                LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] started mode=Idle mediation=LevelPlay gate=Provider rule=- testMode=off consent=none$"));
                yield return BootRelease();
                Assert.AreEqual(HostMode.Idle, _bootstrap.Mode, "the game's flow is not registered: Google's form does not stand in for it");
                Assert.AreEqual(AdResult.NotReady, ShowNow());
                TearDown();
                yield return null; // the first bootstrap is gone before the second wakes

                AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GameFlow, _ => new SimulatorProvider(new NeverScheduler()));
                LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] started mode=Provider mediation=LevelPlay gate=Provider rule=- testMode=off consent=SimulatorProvider\(registered\)$"));
                yield return BootRelease();
                Assert.AreEqual(HostMode.Provider, _bootstrap.Mode, "the game's flow is registered: the SDK starts after it");
            }
            finally
            {
                AdsBootstrap.SwapConsentFlowsForTests(previousConsent.Google, previousConsent.Game); // a later test reads Google's form, registered before the first scene
                AdsBootstrap.BuildForTests = null;
                AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, previous);
            }
        }

        // Issue #164: a release build logged no ad event, so a load without fill, a failed show, a policy block and a
        // refused consent left no line in the player's log. Each cause is warned once, and every failed show.
        [UnityTest]
        public IEnumerator Release_WarnsEachAdFailureOncePerCause_AndEveryFailedShow()
        {
            // the warnings this test causes, in the order it causes them (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\ Ads\]\ NoFill\ Rewarded\ '':\ no\ fill\ \(first\ this\ session;\ repeats\ are\ not\ logged\)"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] ShowFailed Rewarded 'shop_refill'$"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] ShowFailed Rewarded 'shop_refill'$"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\ Ads\]\ PolicyViolation\ UnknownPlacement\ \(Interstitial\ 'no_such_placement'\):\ placement\ 'no_such_placement'"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\ Ads\]\ ConsentRefused:\ the\ consent\ flow\ allows\ no\ ad\ request;\ no\ ads\ until\ the\ player\ changes\ it\ in\ the\ p"));
            SimulatorProvider provider = null;
            var previous = AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, (scheduler, _) => provider = new SimulatorProvider(scheduler, SimulatorScenario.NoFill));
            AdsBootstrap.BuildForTests = BuildKind.Release;
            var previousConsent = AdsBootstrap.SwapConsentFlowsForTests(null, _ => new SimulatorProvider(new NeverScheduler()));
            var warnings = new List<string>();
            Application.LogCallback capture = (message, _, type) => { if (type == LogType.Warning && message.StartsWith("[Nexus Ads] ")) warnings.Add(message); };
            Application.logMessageReceived += capture;
            try
            {
                yield return BootRelease();
                Assert.AreEqual(AdController.Phase.Running, _bootstrap.Controller.State, "the registered flow allows ads: the SDK started");
                provider.Load(AdFormat.Rewarded); // the same no fill again, as the retry after its back-off
                provider.Scenario = SimulatorScenario.ShowFails;
                provider.Load(AdFormat.Rewarded);
                var results = new List<AdResult>();
                _bootstrap.Controller.Show("shop_refill", results.Add);
                _bootstrap.Controller.Show("shop_refill", results.Add); // the failed show reloaded the slot
                _bootstrap.Controller.Show("no_such_placement", results.Add);
                _bootstrap.Controller.Show("no_such_placement", results.Add);
                CollectionAssert.AreEqual(new[] { AdResult.Failed, AdResult.Failed, AdResult.Blocked, AdResult.Blocked }, results);
                CollectionAssert.AreEqual(new[]
                {
                    "[Nexus Ads] NoFill Rewarded '': no fill (first this session; repeats are not logged)",
                    "[Nexus Ads] ShowFailed Rewarded 'shop_refill'",
                    "[Nexus Ads] ShowFailed Rewarded 'shop_refill'",
                    "[Nexus Ads] PolicyViolation UnknownPlacement (Interstitial 'no_such_placement'): placement 'no_such_placement' is not declared in the settings (first for this rule and placement this session; repeats are not logged)",
                }, warnings);
                TearDown();
                yield return null; // the first bootstrap is gone before the second wakes

                warnings.Clear();
                AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GameFlow, _ => new SimulatorProvider(new NeverScheduler(), SimulatorScenario.ConsentRefused));
                yield return BootRelease();
                Assert.AreEqual(AdController.Phase.ConsentRefused, _bootstrap.Controller.State);
                CollectionAssert.AreEqual(new[] { "[Nexus Ads] ConsentRefused: the consent flow allows no ad request; no ads until the player changes it in the privacy options (first this session; repeats are not logged)" }, warnings);
            }
            finally
            {
                Application.logMessageReceived -= capture;
                AdsBootstrap.SwapConsentFlowsForTests(previousConsent.Google, previousConsent.Game);
                AdsBootstrap.BuildForTests = null;
                AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, previous);
            }
        }

        private IEnumerator BootRelease()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Consent = ConsentSource.GameFlow;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.Release.LevelPlay.Android.AppKey = "live";
            _settings.Release.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _root = new GameObject("AdsBootstrapTest");
            _root.SetActive(false);
            _bootstrap = _root.AddComponent<AdsBootstrap>();
            _bootstrap.Configure(_settings, SimulatorScenario.Normal, logEvents: false);
            _root.SetActive(true);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsNotNull(_bootstrap.Controller, "the bootstrap started within 5 s");
        }

        private sealed class NeverScheduler : IAdScheduler
        {
            public System.IDisposable Schedule(double seconds, System.Action action) => null;
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

        // Issue #132: a game with its own UI Toolkit screen turns the IMGUI overlay and debug panel off; the simulated rewarded
        // then stays open until the game's CompleteShow, and the minimum show time holds its close back
        [UnityTest]
        public IEnumerator OverlayAndPanelOff_AddNoIMGUI_OnlyTheGamesCompleteShowEndsTheShow()
        {
            yield return Boot(SimulatorScenario.Normal, simulatorOverlay: false, debugPanel: false);
            foreach (var behaviour in _root.GetComponents<MonoBehaviour>())
                Assert.IsNull(behaviour.GetType().GetMethod("OnGUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                    behaviour.GetType().Name + " draws IMGUI");
            var simulator = _bootstrap.Simulator;
            simulator.MinShowSeconds = 0.2;
            AdFormat? opened = null;
            simulator.Opened += format => opened = format;

            AdResult? result = null;
            _bootstrap.Controller.Show("shop_refill", r => result = r);
            Assert.AreEqual(AdFormat.Rewarded, opened, "the game's screen hears the show open");
            Assert.IsFalse(simulator.CanClose);
            Assert.IsFalse(simulator.CompleteShow(AdResult.Closed), "the minimum holds the close back");
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.IsNull(result, "nothing but the game ends the show");
            Assert.IsTrue(simulator.CanClose, "the minimum has passed");

            Assert.IsTrue(simulator.CompleteShow(AdResult.Rewarded));
            Assert.AreEqual(AdResult.Rewarded, result);
            Assert.AreEqual(1f, Time.timeScale, "the closed show restored the time scale");
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

        // Issue #195: a second bootstrap destroyed its whole GameObject, the game's other components on it with it, and said nothing
        [UnityTest]
        public IEnumerator ASecondBootstrap_RemovesOnlyItsOwnComponent_AndWarnsNamingBothHosts()
        {
            yield return Boot(SimulatorScenario.Normal);
            var host = new GameObject("GameRoot");
            try
            {
                host.SetActive(false);
                var gameComponent = host.AddComponent<BoxCollider>();
                var duplicate = host.AddComponent<AdsBootstrap>();
                LogAssert.Expect(LogType.Warning, "[Nexus Ads] a second AdsBootstrap on 'GameRoot' was removed; the one on 'AdsBootstrapTest' keeps running the ads");
                host.SetActive(true);
                yield return null;
                Assert.IsTrue(host != null && gameComponent != null, "the GameObject and the game's component on it stay");
                Assert.IsTrue(duplicate == null, "the second bootstrap is gone");
                Assert.AreSame(_bootstrap, AdsBootstrap.Instance, "the first one keeps running");
            }
            finally { if (host != null) Object.Destroy(host); }
        }

        // Issue #195: a subscription made while no bootstrap existed was never attached, and nothing said so
        [UnityTest]
        public IEnumerator OnReadyChanged_SubscribedBeforeAnyBootstrapExists_HearsTheOneThatStartsLater_UntilUnsubscribed()
        {
            yield return null; // the previous test's bootstrap is destroyed at the end of its frame
            Assert.IsNull(AdsBootstrap.Instance, "no bootstrap exists yet");
            int changes = 0;
            var unsubscribe = Nexus.Ads.Unity.Ads.OnReadyChanged(() => changes++);
            try
            {
                yield return Boot(SimulatorScenario.Normal);
                yield return null;
                Assert.IsTrue(Nexus.Ads.Unity.Ads.IsReady("shop_refill"), "the simulator filled");
                Assert.Greater(changes, 0, "the fill of the bootstrap that started later reached the handler");

                unsubscribe();
                unsubscribe = null;
                int before = changes, heard = 0;
                var other = Nexus.Ads.Unity.Ads.OnReadyChanged(() => heard++);
                _bootstrap.Simulator.Load(AdFormat.Interstitial);
                yield return null;
                other();
                Assert.Greater(heard, 0, "the load raised ReadyChanged");
                Assert.AreEqual(before, changes, "an unsubscribed handler hears nothing");
            }
            finally { unsubscribe?.Invoke(); }
        }

        // Issue #236: the handlers ran as one multicast, so one that threw stopped every handler after it
        [UnityTest]
        public IEnumerator AThrowingOnReadyChangedHandler_LeavesTheLaterOnesTold_AndIsLogged()
        {
            yield return null; // the previous test's bootstrap is destroyed at the end of its frame
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"^\[Nexus Ads\] HandlerFailed Interstitial '': an Ads\.OnReadyChanged handler threw InvalidOperationException: destroyed view \(first this session"));
            int later = 0;
            var first = Nexus.Ads.Unity.Ads.OnReadyChanged(() => throw new System.InvalidOperationException("destroyed view"));
            var second = Nexus.Ads.Unity.Ads.OnReadyChanged(() => later++);
            try
            {
                yield return Boot(SimulatorScenario.Normal);
                yield return null;
                Assert.Greater(later, 0, "the handler after the throwing one heard the fill");
            }
            finally { first(); second(); }
        }

        [UnityTest]
        public IEnumerator RewardedButton_IsHiddenWhileNothingIsLoaded_AndPaysOnceOnReward()
        {
            // the warnings this test causes, in the order it causes them (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\ Ads\]\ NoFill\ Rewarded\ '':\ no\ fill\ \(first\ this\ session;\ repeats\ are\ not\ logged\)"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\ Ads\]\ NoFill\ Interstitial\ '':\ no\ fill\ \(first\ this\ session;\ repeats\ are\ not\ logged\)"));
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
