using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Tests
{
    /// <summary>One resolver, five statuses, per platform; every surface shows its words.</summary>
    public sealed class ProviderStatusResolverTests
    {
        private AdsSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        [Test]
        public void NoSelection_OrMissingSdk_IsNotInstalled()
        {
            Assert.AreEqual(ProviderStatus.NotInstalled, ProviderStatusResolver.Resolve(_settings, AdProviderKind.None, AdPlatform.Android, true).Status);
            Assert.AreEqual(ProviderStatus.NotInstalled, ProviderStatusResolver.Resolve(_settings, AdProviderKind.LevelPlay, AdPlatform.Android, false).Status);
        }

        [Test]
        public void EmptyRelease_WithoutOfficialTestData_IsInactive_AndNamesTheFields()
        {
            var report = ProviderStatusResolver.Resolve(_settings, AdProviderKind.LevelPlay, AdPlatform.Android, true);
            Assert.AreEqual(ProviderStatus.Inactive, report.Status);
            CollectionAssert.AreEqual(new[] { "App Key", "rewarded unit id" }, report.Missing);
            Assert.IsFalse(report.IncludedInRelease);
        }

        [Test]
        public void EmptyRelease_WithGoogleSampleTestData_IsTestOnly()
        {
            _settings.Test.AdMob.Android.AppKey = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Test.AdMob.Android.RewardedUnitId = AdMobSampleIds.PublisherPrefix + "/5224354917";
            var report = ProviderStatusResolver.Resolve(_settings, AdProviderKind.AdMob, AdPlatform.Android, true);
            Assert.AreEqual(ProviderStatus.TestOnly, report.Status);
            Assert.IsFalse(report.IncludedInRelease);
        }

        [Test]
        public void CompleteRelease_IsActive_PerPlatform()
        {
            _settings.Release.LevelPlay.Android.AppKey = "key";
            _settings.Release.LevelPlay.Android.RewardedUnitId = "rewarded";
            Assert.AreEqual(ProviderStatus.Active, ProviderStatusResolver.Resolve(_settings, AdProviderKind.LevelPlay, AdPlatform.Android, true).Status);
            Assert.AreEqual(ProviderStatus.Inactive, ProviderStatusResolver.Resolve(_settings, AdProviderKind.LevelPlay, AdPlatform.Ios, true).Status, "iOS is judged on its own keys");
        }

        [Test]
        public void SampleIdsOnTheReleaseTab_AreInvalid()
        {
            _settings.Release.AdMob.Android.AppKey = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Release.AdMob.Android.RewardedUnitId = "ca-app-pub-1234567890123456/1234567890";
            var report = ProviderStatusResolver.Resolve(_settings, AdProviderKind.AdMob, AdPlatform.Android, true);
            Assert.AreEqual(ProviderStatus.Invalid, report.Status);
            StringAssert.Contains("sample", report.Reason);
        }

        [Test]
        public void MalformedAdMobId_IsInvalid_WellFormedIsActive()
        {
            _settings.Release.AdMob.Android.AppKey = "ca-app-pub-1234567890123456~1234567890";
            _settings.Release.AdMob.Android.RewardedUnitId = "not-an-id";
            Assert.AreEqual(ProviderStatus.Invalid, ProviderStatusResolver.Resolve(_settings, AdProviderKind.AdMob, AdPlatform.Android, true).Status);
            _settings.Release.AdMob.Android.RewardedUnitId = "ca-app-pub-1234567890123456/1234567890";
            Assert.AreEqual(ProviderStatus.Active, ProviderStatusResolver.Resolve(_settings, AdProviderKind.AdMob, AdPlatform.Android, true).Status);
        }
    }
}
