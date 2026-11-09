#if UNITY_EDITOR && NEXUS_LEVELPLAY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Nexus.Ads;
using Nexus.Ads.LevelPlay;
using NUnit.Framework;
using Unity.Services.LevelPlay;
using UnityEditor.PackageManager;
using UnityEngine;
using AdFormat = Nexus.Ads.AdFormat;

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsLevelPlaySdkTests
    {
        [Test]
        public void A03_LevelPlayRewardBeyondCloseGraceRemainsRecoverable()
        {
            using var rig = new Rig();
            rig.DelayReward(LevelPlayProvider.RewardGraceSeconds + 0.5);
            rig.Controller.Show("first", _ => { });
            rig.CloseMockRewarded();

            rig.Scheduler.Advance(LevelPlayProvider.RewardGraceSeconds + 0.1);
            AdResult? nextResult = null;
            rig.Controller.Show("second", result => nextResult = result);
            rig.Scheduler.Advance(1);

            Assert.That(rig.Controller.PendingReward?.Placement, Is.EqualTo("first"),
                "The SDK's reward must survive a close/reward delay greater than the provider's fixed grace period.");
            Assert.That(nextResult, Is.Null, "A retired ad's reward must not complete the replacement.");
            Assert.That(rig.OwnedPrefabs().Count(item => item.GetType().Name == "RewardedPrefab"), Is.EqualTo(1),
                "Only the replacement SDK object may remain owned.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            rig.RepeatOriginalReward();
            Assert.That(rig.Controller.PendingReward, Is.Null, "Duplicate SDK reward delivery must not recreate the receipt.");
        }

        [Test]
        public void A06_DisposingLevelPlayDestroysOwnedRewardedAndBannerObjects()
        {
            using var rig = new Rig();
            rig.Controller.ShowBanner("menu", BannerPosition.Bottom);
            var owned = rig.OwnedPrefabs();
            Assert.That(owned.Length, Is.EqualTo(2), "The SDK editor mock must own one rewarded object and one banner.");

            rig.Controller.Dispose();
            rig.Provider.Dispose();
            rig.Provider.Dispose();
            rig.Provider.Load(AdFormat.Rewarded);
            rig.Provider.ShowBanner("menu", BannerPosition.Bottom);

            Assert.That(owned.All(prefab => prefab == null), Is.True,
                "Provider teardown must destroy its SDK objects before the fixture's fallback cleanup.");
            Assert.That(rig.OwnedPrefabs(), Is.Empty, "No SDK object may be recreated after disposal.");
        }

        private sealed class Rig : IDisposable
        {
            private readonly HashSet<UnityEngine.Object> _before;
            private readonly object _editorSdk;
            private readonly FieldInfo _initializedField;
            private readonly bool _wasInitialized;
            private FieldInfo _rewardField;
            private LevelPlayRewardedAd _sdkRewarded;
            private object _originalRewardHandler;
            private LevelPlayReward _observedReward;
            public readonly LevelPlayProvider Provider;
            public readonly AdController Controller;
            public readonly Scheduler Scheduler;

            public Rig()
            {
                if (!Application.isPlaying) Assert.Inconclusive("Run this SDK fixture in PlayMode.");
#if !UNITY_ANDROID && !UNITY_IOS
                Assert.Inconclusive("The locked SDK's editor mock requires an Android or iOS build target.");
#endif
                var package = PackageInfo.FindForAssembly(typeof(LevelPlayRewardedAd).Assembly);
                if (package == null || package.version != "9.6.0")
                    Assert.Inconclusive("This fixture requires the locked com.unity.services.levelplay 9.6.0 package.");
                SourceIdentity(package.resolvedPath, "Runtime/Api/LevelPlaySdkProvider.cs",
                    "CF9C75B1100F60C8BAF78B034882C2C7B5B6C8B01FA1D67C1F1D0D8C91967B97");
                SourceIdentity(package.resolvedPath, "Runtime/Platforms/Editor/EditorLevelPlaySdk.cs",
                    "FDE3B7BD2A5130111571516A2125BCCEBF4DAFE4B9B0F7521E4EF5ECB9E3C799");
                SourceIdentity(package.resolvedPath, "Runtime/Api/LevelPlayRewardedAd.cs",
                    "2D3E58622B9F0C0DDEEBF8847C79A4E6B13263A059137CA223343126CB85CE22");
                SourceIdentity(package.resolvedPath, "Runtime/Platforms/Editor/EditorRewardedAd.cs",
                    "F66A807A1D69C6DEE2B81AADCB15348C6B8E859F63436EE8D537FBCC5CA00D27");
                SourceIdentity(package.resolvedPath, "Runtime/Platforms/Editor/EditorBannerAd.cs",
                    "ADE85AE48B793FD4BCDF09A6AFF674CA7DD514EFCA9E9F0DB5B37FDEE1427372");

                var sdkProvider = typeof(LevelPlayRewardedAd).Assembly.GetType("Unity.Services.LevelPlay.LevelPlaySdkProvider", true);
                _editorSdk = sdkProvider.GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                Assert.That(_editorSdk?.GetType().FullName, Is.EqualTo("Unity.Services.LevelPlay.EditorLevelPlaySdk"),
                    "Never initialize a device SDK from this fixture.");
                _initializedField = _editorSdk.GetType().GetField("m_IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
                _wasInitialized = (bool)_initializedField.GetValue(_editorSdk);
                _before = new HashSet<UnityEngine.Object>(Prefabs());
                Assert.That(MediationLock.Holder, Is.Null, "Run this fixture with no other mediation controller alive.");
                var clock = new Clock();
                Scheduler = new Scheduler(clock);
                Provider = new LevelPlayProvider(Scheduler, () => true);
                var config = new AdsConfig(
                    new[] { new AdPlacement("first", AdFormat.Rewarded), new AdPlacement("second", AdFormat.Rewarded),
                        new AdPlacement("menu", AdFormat.Banner) },
                    AdPolicy.Default, new ProviderConfig("fixture-app",
                        new Dictionary<AdFormat, string> { { AdFormat.Rewarded, "fixture-reward" }, { AdFormat.Banner, "fixture-banner" } },
                        false, null, false, false, false, "G"), BuildKind.Editor);
                Controller = new AdController(config, Provider, clock, new Visibility(), new MainThread(),
                    Scheduler, new Store(), new Telemetry());
                Controller.Start();
                Assert.That(Controller.IsReady("first"), Is.True, "The real SDK editor mock must load the ad.");
            }

            public void DelayReward(double seconds)
            {
                _sdkRewarded = (LevelPlayRewardedAd)typeof(LevelPlayProvider)
                    .GetField("_rewarded", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Provider);
                _rewardField = typeof(LevelPlayRewardedAd).GetField("OnAdRewarded", BindingFlags.Instance | BindingFlags.NonPublic);
                _originalRewardHandler = _rewardField.GetValue(_sdkRewarded);
                var original = (Action<LevelPlayAdInfo, LevelPlayReward>)_originalRewardHandler;
                Assert.That(original, Is.Not.Null, "The provider must be subscribed at the real SDK event boundary.");
                Action<LevelPlayAdInfo, LevelPlayReward> delayed = (info, reward) =>
                {
                    _observedReward = reward;
                    Scheduler.Schedule(seconds, () => original(info, reward));
                };
                _rewardField.SetValue(_sdkRewarded, delayed);
            }

            public void CloseMockRewarded()
            {
                var prefab = OwnedPrefabs().Single(item => item.GetType().Name == "RewardedPrefab");
                prefab.GetType().GetMethod("HideAd", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(prefab, null);
            }

            public void RepeatOriginalReward()
            {
                Assert.That(_observedReward, Is.Not.Null, "Replay the actual SDK payload, not a fabricated reward.");
                ((Action<LevelPlayAdInfo, LevelPlayReward>)_originalRewardHandler)(null, _observedReward);
            }

            public UnityEngine.Object[] OwnedPrefabs() => Prefabs().Where(prefab => !_before.Contains(prefab)).ToArray();

            public void Dispose()
            {
                if (_rewardField != null) _rewardField.SetValue(_sdkRewarded, _originalRewardHandler);
                Controller.Dispose();
                Provider.Dispose();
                Scheduler.CancelAll();
                foreach (var prefab in OwnedPrefabs())
                    if (prefab is Component component && component != null) UnityEngine.Object.DestroyImmediate(component.gameObject);
                _initializedField.SetValue(_editorSdk, _wasInitialized);
            }

            private static IEnumerable<UnityEngine.Object> Prefabs()
            {
                var assembly = typeof(LevelPlayRewardedAd).Assembly;
                foreach (string name in new[] { "RewardedPrefab", "BannerPrefab" })
                    foreach (var prefab in UnityEngine.Object.FindObjectsByType(assembly.GetType("Unity.Services.LevelPlay." + name, true)))
                        yield return prefab;
            }

            private static void SourceIdentity(string root, string relative, string expected)
            {
                string path = Path.Combine(root, relative);
                if (!File.Exists(path)) Assert.Inconclusive("The pinned SDK source is unavailable: " + relative);
                using var hash = SHA256.Create();
                using var file = File.OpenRead(path);
                string actual = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
                if (actual != expected) Assert.Inconclusive("The pinned SDK source changed; re-audit the test boundary: " + relative);
            }
        }

        private sealed class Clock : IAdClock
        {
            public double Seconds { get; set; } = 1000;
            public long UtcDay => 100;
            public double UtcSeconds => 8640000 + Seconds;
        }

        private sealed class Scheduler : IAdScheduler
        {
            private readonly Clock _clock;
            private readonly List<Entry> _pending = new List<Entry>();
            public Scheduler(Clock clock) => _clock = clock;
            public IDisposable Schedule(double seconds, Action action)
            {
                var entry = new Entry { At = _clock.Seconds + seconds, Action = action };
                _pending.Add(entry);
                return entry;
            }
            public void Advance(double seconds)
            {
                double end = _clock.Seconds + seconds;
                Entry next;
                while ((next = _pending.Where(item => !item.Done && item.At <= end).OrderBy(item => item.At).FirstOrDefault()) != null)
                {
                    next.Done = true;
                    _clock.Seconds = next.At;
                    next.Action();
                }
                _clock.Seconds = end;
            }
            public void CancelAll() { foreach (var entry in _pending) entry.Dispose(); }
            private sealed class Entry : IDisposable
            {
                public double At;
                public Action Action;
                public bool Done;
                public void Dispose() => Done = true;
            }
        }

        private sealed class Visibility : IAppVisibility
        {
            public bool Visible => true;
            public event Action<bool> Changed { add { } remove { } }
        }

        private sealed class MainThread : IMainThread
        {
            public bool IsCurrent => true;
            public void Post(Action action) => action();
        }

        private sealed class Store : IPacingStore
        {
            private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();
            public string Get(string key) => _rows.TryGetValue(key, out string value) ? value : null;
            public void Set(string key, string value) => _rows[key] = value;
        }

        private sealed class Telemetry : IAdTelemetry { public void Record(in AdEvent e) { } }
    }
}
#endif
