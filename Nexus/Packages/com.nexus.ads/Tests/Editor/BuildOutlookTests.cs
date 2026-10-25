using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Tests
{
    /// <summary>The words every surface shows for a build are the decision the bootstrap runs, never a promise it will not keep.</summary>
    public sealed class BuildOutlookTests
    {
        private const string Phone = "5de44135-0000-0000-0000-000000000000";
        private AdsSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        private sealed class NoScheduler : IAdScheduler
        {
            public static readonly NoScheduler Instance = new NoScheduler();
            public System.IDisposable Schedule(double seconds, System.Action action) => null;
        }

        [Test]
        public void ASharedConsentPlatform_RunsBeforeAMediationWithoutAForm_AndTheProviderThatRunsItKeepsItsOwn()
        {
            // issue #63: com.nexus.ads.admob registers Google UMP; LevelPlay's own flow is a pass-through
            var config = _settings.ToConfig(BuildKind.Development, AdPlatform.Android).Provider;
            var levelPlay = new SimulatorProvider(NoScheduler.Instance);
            Assert.AreSame(levelPlay, AdsBootstrap.ConsentFlowFor(AdProviderKind.LevelPlay, levelPlay, config), "nothing registered: the provider's own flow");

            var shared = new SimulatorProvider(NoScheduler.Instance);
            ProviderConfig seen = null;
            AdsBootstrap.RegisterSharedConsentFlow(c => { seen = c; return shared; }, AdProviderKind.AdMob);
            try
            {
                Assert.AreSame(shared, AdsBootstrap.ConsentFlowFor(AdProviderKind.LevelPlay, levelPlay, config));
                Assert.AreSame(config, seen, "the flow gets the provider configuration (age treatment, test mode)");
                var admob = new SimulatorProvider(NoScheduler.Instance);
                Assert.AreSame(admob, AdsBootstrap.ConsentFlowFor(AdProviderKind.AdMob, admob, config), "the AdMob provider runs UMP itself");
            }
            finally { AdsBootstrap.RegisterSharedConsentFlow(null, AdProviderKind.None); }
            Assert.AreSame(levelPlay, AdsBootstrap.ConsentFlowFor(AdProviderKind.LevelPlay, levelPlay, config), "a null factory clears the registration");
        }

        private void FillLevelPlay()
        {
            foreach (var tab in new[] { _settings.Test, _settings.Release })
            {
                tab.LevelPlay.Android.AppKey = "key";
                tab.LevelPlay.Android.RewardedUnitId = "rewarded";
            }
        }

        private IReadOnlyList<OutlookRow> Rows(bool installed = true)
        {
            var status = ProviderStatusResolver.Resolve(_settings, _settings.SelectedMediation, AdPlatform.Android, installed);
            return BuildOutlook.Rows(_settings, AdPlatform.Android, installed, status);
        }

        private static OutlookRow Find(IReadOnlyList<OutlookRow> rows, string where)
        {
            foreach (var row in rows) if (row.Where == where) return row;
            Assert.Fail("no row '" + where + "' in: " + string.Join(" | ", System.Linq.Enumerable.Select(rows, r => r.Where)));
            return default;
        }

        [Test]
        public void TheEditorRow_IsTheSimulator_AndSaysItProvesNothingAboutAStoreBuild()
        {
            FillLevelPlay();
            var row = Find(Rows(), "Editor (Play mode)");
            Assert.AreEqual(HostMode.Simulator, row.Mode);
            StringAssert.Contains("prove nothing about a store build", row.Text);
        }

        [Test]
        public void LevelPlayDevelopment_ListedDeviceRunsTheSdk_AnyOtherDeviceTheSimulator_InTwoRows()
        {
            FillLevelPlay();
            _settings.TestDeviceIds.Add(" " + Phone + " ");
            var rows = Rows();
            var listed = Find(rows, "Development build, listed device");
            var other = Find(rows, "Development build, other devices");
            Assert.AreEqual(HostMode.Provider, listed.Mode);
            StringAssert.Contains("starts with the account keys", listed.Text);
            StringAssert.Contains("also in the LevelPlay dashboard's test device list", listed.Text); // the runtime cannot see the dashboard, so it never promises test ads here
            Assert.AreEqual(HostMode.Simulator, other.Mode);
            StringAssert.Contains("not in the Test tab's device list", other.Text);
        }

        [Test]
        public void LevelPlayDevelopment_WithNoListedDevice_RunsTheSimulatorEverywhere()
        {
            FillLevelPlay();
            var row = Find(Rows(), "Development build");
            Assert.AreEqual(HostMode.Simulator, row.Mode);
            StringAssert.Contains("device list", row.Text);
        }

        [Test]
        public void AdMobSampleUnits_RunTheSdkOnEveryDevice_SoDevelopmentIsOneRow()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            _settings.Test.AdMob.Android.AppKey = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Test.AdMob.Android.RewardedUnitId = AdMobSampleIds.PublisherPrefix + "/5224354917";
            _settings.TestDeviceIds.Add(Phone);
            var row = Find(Rows(), "Development build, any device");
            Assert.AreEqual(HostMode.Provider, row.Mode);
            StringAssert.Contains("Google's sample units", row.Text);
        }

        [Test]
        public void ADevelopmentTestTabWithMissingKeys_SaysNoAdLoads_InsteadOfPromisingTestAds()
        {
            _settings.TestDeviceIds.Add(Phone);
            var row = Find(Rows(), "Development build, listed device");
            Assert.AreEqual(HostMode.Provider, row.Mode);
            Assert.IsTrue(row.Problem);
            StringAssert.Contains("Test tab misses App Key", row.Text);
            StringAssert.Contains("no ad loads", row.Text);
        }

        [Test]
        public void ReleaseWithCompleteKeys_IsLive_AndWithMissingKeys_SaysTheValidatorStopsTheBuild()
        {
            FillLevelPlay();
            var live = Find(Rows(), "Release build");
            Assert.AreEqual(HostMode.Provider, live.Mode);
            StringAssert.Contains("live ads", live.Text);
            Assert.IsFalse(live.Problem);

            _settings.Release.LevelPlay.Android.RewardedUnitId = "";
            var stopped = Find(Rows(), "Release build");
            Assert.IsTrue(stopped.Problem);
            StringAssert.Contains("rewarded unit id missing", stopped.Text);
            StringAssert.Contains("validator stops this build", stopped.Text);
        }

        [Test]
        public void AdFreeRelease_IsNoAdsOnPurpose_AndNotAProblem()
        {
            FillLevelPlay();
            _settings.AdFreeRelease = true;
            var row = Find(Rows(), "Release build");
            Assert.AreEqual(HostMode.Idle, row.Mode);
            Assert.IsFalse(row.Problem);
            StringAssert.Contains("on purpose", row.Text);
        }

        [Test]
        public void ProviderNotInstalled_RunsNothingOnAListedDevice_TheSimulatorOnAnUnlistedOne_AndStopsTheRelease()
        {
            FillLevelPlay();
            _settings.TestDeviceIds.Add(Phone);
            var rows = Rows(installed: false);
            var listed = Find(rows, "Development build, listed device");
            Assert.AreEqual(HostMode.Idle, listed.Mode);
            StringAssert.Contains("provider is not registered", listed.Text);
            Assert.AreEqual(HostMode.Simulator, Find(rows, "Development build, other devices").Mode);
            var release = Find(rows, "Release build");
            Assert.AreEqual(HostMode.Idle, release.Mode);
            StringAssert.Contains("validator stops this build", release.Text);
        }

        [Test]
        public void NoMediationSelected_InDevelopmentIsTheSimulator_InReleaseNothing()
        {
            _settings.SelectedMediation = AdProviderKind.None;
            var rows = Rows(installed: false);
            Assert.AreEqual(HostMode.Simulator, Find(rows, "Development build").Mode);
            var release = Find(rows, "Release build");
            Assert.AreEqual(HostMode.Idle, release.Mode);
            StringAssert.Contains("no mediation is selected", release.Text);
        }
    }
}
