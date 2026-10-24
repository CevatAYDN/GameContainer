using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Tests
{
    /// <summary>The settings asset turns into the core's configuration the way the brief fixes it: tab by build kind, fields by placements, floors never lowered.</summary>
    public sealed class AdsSettingsTests
    {
        private AdsSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.Release.LevelPlay.Android.AppKey = "live-key";
            _settings.Release.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _settings.Test.LevelPlay.Android.AppKey = "test-key";
            _settings.Test.LevelPlay.Android.RewardedUnitId = "test-rewarded";
            _settings.TestDeviceIds.Add("gaid-1");
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_settings);

        [Test]
        public void EditorAndDevelopmentBuilds_ReadTheTestTab_ReleaseReadsTheReleaseTab()
        {
            Assert.AreEqual("test-key", _settings.ToConfig(BuildKind.Editor, AdPlatform.Android).Provider.AppKey);
            Assert.AreEqual("test-key", _settings.ToConfig(BuildKind.Development, AdPlatform.Android).Provider.AppKey);
            Assert.AreEqual("live-key", _settings.ToConfig(BuildKind.Release, AdPlatform.Android).Provider.AppKey);
        }

        [Test]
        public void TestTab_CarriesTestModeAndDevices_ReleaseTabCarriesNeither()
        {
            var test = _settings.ToProviderConfig(BuildKind.Development, AdPlatform.Android, AdProviderKind.LevelPlay);
            var release = _settings.ToProviderConfig(BuildKind.Release, AdPlatform.Android, AdProviderKind.LevelPlay);
            Assert.IsTrue(test.TestMode);
            Assert.AreEqual(1, test.TestDeviceIds.Count);
            Assert.IsFalse(release.TestMode);
            Assert.AreEqual(0, release.TestDeviceIds.Count);
            Assert.AreEqual(GateDecision.Provider, ProviderGate.Evaluate(BuildKind.Release, release, "any").Decision);
        }

        [Test]
        public void AdMobTestTab_WithGoogleSampleIds_IsPublishedTestData_WithoutTestMode()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            _settings.Test.AdMob.Android.AppKey = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Test.AdMob.Android.RewardedUnitId = AdMobSampleIds.PublisherPrefix + "/5224354917";
            var config = _settings.ToProviderConfig(BuildKind.Development, AdPlatform.Android, AdProviderKind.AdMob);
            Assert.IsTrue(config.UsesPublishedTestData);
            Assert.IsFalse(config.TestMode);
            Assert.AreEqual(GateDecision.Provider, ProviderGate.Evaluate(BuildKind.Development, config, "unlisted").Decision, "sample units are safe on every device");
        }

        [Test]
        public void MissingFields_ComeFromThePlacements_NotFromAFixedList()
        {
            var missing = _settings.MissingFields(AdProviderKind.LevelPlay, AdPlatform.Ios, _settings.Release);
            CollectionAssert.AreEqual(new[] { "App Key", "rewarded unit id" }, missing, "no interstitial placement, so no interstitial id is required");
            _settings.Placements.Add(new PlacementEntry { Name = "level_end", Format = AdFormat.Interstitial, NaturalBreak = true });
            CollectionAssert.Contains(_settings.MissingFields(AdProviderKind.LevelPlay, AdPlatform.Android, _settings.Release), "interstitial unit id");
        }

        [Test]
        public void PolicyFloors_BelowTheDefaults_AreRaisedToTheDefaults()
        {
            _settings.Policy.MinInterstitialGapSeconds = 1;
            _settings.Policy.NoInterstitialForFirstSeconds = 300;
            var policy = _settings.Policy.ToPolicy();
            Assert.AreEqual(AdPolicy.Default.MinInterstitialGapSeconds, policy.MinInterstitialGapSeconds);
            Assert.AreEqual(300, policy.NoInterstitialForFirstSeconds);
        }

        [Test]
        public void DuplicatePlacementNames_AreRejected()
        {
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            Assert.Throws<ArgumentException>(() => _settings.ToConfig(BuildKind.Editor, AdPlatform.Android));
        }

        [Test]
        public void SamplePublisherPrefix_EqualsTheCatalog()
        {
            string path = Path.GetFullPath("Packages/com.nexus.ads/Editor/PlatformCatalog.json");
            var match = Regex.Match(File.ReadAllText(path), "\"publisherPrefix\":\\s*\"([^\"]+)\"");
            Assert.IsTrue(match.Success, "catalog has a publisherPrefix");
            Assert.AreEqual(match.Groups[1].Value, AdMobSampleIds.PublisherPrefix);
        }
    }
}
