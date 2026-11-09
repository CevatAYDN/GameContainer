#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Nexus.Ads;
using Nexus.Ads.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsUnityLifecycleTests
    {
        [UnityTest]
        public IEnumerator A01_RewardedButtonDoesNotAcknowledgeAReentrantFailedGrant()
        {
            using var rig = new Rig();
            yield return rig.Start();
            var buttonObject = new GameObject("FullReviewAdsButton", typeof(RectTransform), typeof(CanvasGroup), typeof(Button));
            buttonObject.transform.SetParent(rig.Root.transform);
            var rewarded = buttonObject.AddComponent<AdRewardedButton>();
            rewarded.Placement = Rig.Placement;
            string newerId = null;
            rewarded.OnRewarded.AddListener(() =>
            {
                rig.Bootstrap.Controller.Show(Rig.Placement, result =>
                {
                    Assert.That(result, Is.EqualTo(AdResult.Rewarded));
                    newerId = rig.Bootstrap.Controller.DeliveringReward.Value.Id;
                    throw new IOException("the reentrant grant did not commit");
                });
                Assert.Throws<IOException>(() => rig.Bootstrap.Simulator.CompleteShow(AdResult.Rewarded));
            });

            buttonObject.GetComponent<Button>().onClick.Invoke();
            Assert.That(rig.Bootstrap.Simulator.CompleteShow(AdResult.Rewarded), Is.True);

            Assert.That(newerId, Is.Not.Null, "The nested show must have produced its own receipt.");
            Assert.That(rig.Bootstrap.Controller.PendingReward?.Id, Is.EqualTo(newerId),
                "The outer button must acknowledge its captured delivery, not the failed nested grant.");
        }

        [UnityTest]
        public IEnumerator A04_DestroyingTheBootstrapRestoresThePauseStateItOwns()
        {
            using var rig = new Rig();
            yield return rig.Start();
            Time.timeScale = 0.625f;
            AudioListener.pause = false;
            rig.Bootstrap.Controller.Show(Rig.Placement, _ => { });
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(AudioListener.pause, Is.True);

            UnityEngine.Object.Destroy(rig.Root);
            yield return null;

            Assert.That(Time.timeScale, Is.EqualTo(0.625f),
                "Destroying the ad owner must restore the game's previous time scale before fixture cleanup.");
            Assert.That(AudioListener.pause, Is.False,
                "Destroying the ad owner must restore the game's previous audio pause before fixture cleanup.");
        }

        [UnityTest]
        public IEnumerator A06_DestroyingBootstrapDisposesItsFactoryCreatedConsentFlow()
        {
            var consent = new DeferredConsent();
            using var rig = new Rig(consent);
            yield return rig.Start();
            Assert.That(rig.Bootstrap.Controller.State, Is.EqualTo(AdController.Phase.AwaitingConsent));
            Assert.That(consent.Requests, Is.EqualTo(1));

            UnityEngine.Object.Destroy(rig.Root);
            yield return null;

            Assert.That(consent.Disposals, Is.EqualTo(1),
                "The bootstrap created the separate flow through its factory and must release that flow on destruction.");
        }

        private sealed class Rig : IDisposable
        {
            public const string Placement = "full_review_ads_reward";
            private readonly float _timeScale = Time.timeScale;
            private readonly bool _audioPaused = AudioListener.pause;
            private readonly BuildKind? _build = AdsBootstrap.BuildForTests;
            private readonly Dictionary<string, string> _saved = new Dictionary<string, string>();
            private readonly AdsSettings _settings;
            private readonly bool _customConsent;
            private readonly Func<IAdScheduler, ProviderConfig, IAdProvider> _previousProvider;
            private readonly (Func<ProviderConfig, IConsentFlow> Google, Func<ProviderConfig, IConsentFlow> Game) _previousFlows;
            public readonly GameObject Root;
            public readonly AdsBootstrap Bootstrap;

            public Rig(DeferredConsent consent = null)
            {
                Assert.That(AdsBootstrap.Instance, Is.Null, "Run this fixture in an isolated PlayMode test scene.");
                foreach (string key in new[]
                {
                    "nexus.ads.reward.pending",
                    "nexus.ads.sessions.lifetime",
                    "nexus.ads.interstitials.day",
                    "nexus.ads.day.interstitials",
                    "nexus.ads.last.rewarded",
                    "nexus.ads.last.placement." + Placement,
                    "nexus.ads.day." + Placement,
                })
                    _saved[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
                PlayerPrefs.DeleteKey("nexus.ads.reward.pending");
                PlayerPrefs.Save();

                _customConsent = consent != null;
                AdsBootstrap.BuildForTests = _customConsent ? BuildKind.Release : BuildKind.Editor;
                _settings = ScriptableObject.CreateInstance<AdsSettings>();
                _settings.SelectedMediation = AdProviderKind.None;
                _settings.Placements.Add(new PlacementEntry { Name = Placement, Format = AdFormat.Rewarded });
                if (_customConsent)
                {
                    _settings.SelectedMediation = AdProviderKind.LevelPlay;
                    _settings.Consent = ConsentSource.GameFlow;
                    _settings.Release.LevelPlay.Android.AppKey = "fixture-app";
                    _settings.Release.LevelPlay.Android.RewardedUnitId = "fixture-reward";
                    _previousProvider = AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay,
                        (scheduler, _) => new SimulatorProvider(scheduler));
                    _previousFlows = AdsBootstrap.SwapConsentFlowsForTests(null, _ => consent);
                }
                Root = new GameObject("FullReviewAdsBootstrap");
                Root.SetActive(false);
                Bootstrap = Root.AddComponent<AdsBootstrap>();
                Bootstrap.Configure(_settings, logEvents: false, debugPanel: false);
            }

            public IEnumerator Start()
            {
                Root.SetActive(true);
                float deadline = Time.realtimeSinceStartup + 5;
                while (Bootstrap.Controller == null && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(Bootstrap.Controller, Is.Not.Null, "The simulator bootstrap must start before the regression runs.");
                Assert.That(Bootstrap.Mode, Is.EqualTo(_customConsent ? HostMode.Provider : HostMode.Simulator));
                Assert.That(Bootstrap.Provider, Is.InstanceOf<SimulatorProvider>(), "This fixture must never start an advertising SDK.");
                if (!_customConsent) Assert.That(Bootstrap.Controller.IsReady(Placement), Is.True);
            }

            public void Dispose()
            {
                if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
                if (_settings != null) UnityEngine.Object.DestroyImmediate(_settings);
                AdsBootstrap.BuildForTests = _build;
                if (_customConsent)
                {
                    AdsBootstrap.SwapProviderForTests(AdProviderKind.LevelPlay, _previousProvider);
                    AdsBootstrap.SwapConsentFlowsForTests(_previousFlows.Google, _previousFlows.Game);
                }
                Time.timeScale = _timeScale;
                AudioListener.pause = _audioPaused;
                foreach (var row in _saved)
                {
                    if (row.Value == null) PlayerPrefs.DeleteKey(row.Key);
                    else PlayerPrefs.SetString(row.Key, row.Value);
                }
                PlayerPrefs.Save();
            }
        }

        private sealed class DeferredConsent : IConsentFlow, IDisposable
        {
            public int Requests, Disposals;
            public bool Completed => false;
            public bool CanRequestAds => false;
            public bool PrivacyOptionsRequired => false;
            public event Action Changed { add { } remove { } }
            public void Request(Action completed) => Requests++;
            public void ShowPrivacyOptions(Action closed) { }
            public void Dispose() => Disposals++;
        }
    }
}
#endif
