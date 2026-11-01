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
        public void TheRegisteredFlowTheSettingsName_RunsBeforeLevelPlay_AndAdMobKeepsItsOwn()
        {
            // issue #63: com.nexus.ads.admob registers Google UMP; LevelPlay's own flow is a pass-through. Issue #181: each source has
            // its own slot, and only the flow the settings name runs
            var config = _settings.ToConfig(BuildKind.Development, AdPlatform.Android).Provider;
            var levelPlay = new SimulatorProvider(NoScheduler.Instance);
            var google = new SimulatorProvider(NoScheduler.Instance);
            var game = new SimulatorProvider(NoScheduler.Instance);
            ProviderConfig seen = null;
            var previous = AdsBootstrap.SwapConsentFlowsForTests(c => { seen = c; return google; }, c => game);
            try
            {
                _settings.Consent = ConsentSource.GoogleUmp;
                Assert.AreSame(google, AdsBootstrap.ConsentFlowFor(_settings, levelPlay, config));
                Assert.AreSame(config, seen, "the flow gets the provider configuration (age treatment, test mode)");
                _settings.Consent = ConsentSource.GameFlow;
                Assert.AreSame(game, AdsBootstrap.ConsentFlowFor(_settings, levelPlay, config), "the game's flow, although Google's form is registered too");
                _settings.Consent = ConsentSource.None;
                Assert.AreSame(levelPlay, AdsBootstrap.ConsentFlowFor(_settings, levelPlay, config), "None: LevelPlay's pass-through; Google's form does not run");

                AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GoogleUmp, null);
                _settings.Consent = ConsentSource.GameFlow;
                Assert.AreSame(game, AdsBootstrap.ConsentFlowFor(_settings, levelPlay, config), "clearing Google's form leaves the game's flow registered");

                _settings.SelectedMediation = AdProviderKind.AdMob;
                var admob = new SimulatorProvider(NoScheduler.Instance);
                Assert.AreSame(admob, AdsBootstrap.ConsentFlowFor(_settings, admob, config), "the AdMob provider runs UMP itself");
            }
            finally { AdsBootstrap.SwapConsentFlowsForTests(previous.Google, previous.Game); }
        }

        [Test]
        public void TheStartLine_NamesTheConsentFlowThatRunsAndWhereItCameFrom()
        {
            // issue #154: a release build that started LevelPlay with only its pass-through left no trace of it
            var flow = new SimulatorProvider(NoScheduler.Instance);
            _settings.Consent = ConsentSource.None;
            Assert.AreEqual("SimulatorProvider(provider)", AdsBootstrap.DescribeConsent(_settings, HostMode.Provider, flow), "Consent: None: the provider's own flow");
            Assert.AreEqual("SimulatorProvider(simulator)", AdsBootstrap.DescribeConsent(_settings, HostMode.Simulator, flow));
            Assert.AreEqual("none", AdsBootstrap.DescribeConsent(_settings, HostMode.Idle, flow), "an idle host runs no flow");
            _settings.Consent = ConsentSource.GameFlow;
            Assert.AreEqual("SimulatorProvider(registered)", AdsBootstrap.DescribeConsent(_settings, HostMode.Provider, flow));
            _settings.SelectedMediation = AdProviderKind.AdMob;
            Assert.AreEqual("SimulatorProvider(provider)", AdsBootstrap.DescribeConsent(_settings, HostMode.Provider, flow), "the AdMob provider runs UMP itself");
        }

        [Test]
        public void LevelPlay_StartsNoAds_WithoutTheRegisteredFlowTheSettingsName()
        {
            // issue #154: LevelPlay ships no consent form, and a provider host started it with only its pass-through. Issue #181: any
            // registered flow satisfied the setting, so Google's form, which com.nexus.ads.admob registers, ran for a game that named
            // its own flow and never registered it
            _settings.Consent = ConsentSource.GoogleUmp;
            StringAssert.Contains("com.nexus.ads.admob", BuildOutlook.MissingConsent(_settings, false, false));
            StringAssert.Contains("runs only with Consent: GameFlow", BuildOutlook.MissingConsent(_settings, false, true), "the game's flow is not Google's form");
            Assert.IsNull(BuildOutlook.MissingConsent(_settings, true, false));

            _settings.Consent = ConsentSource.GameFlow;
            StringAssert.Contains("RegisterSharedConsentFlow", BuildOutlook.MissingConsent(_settings, false, false));
            StringAssert.Contains("runs only with Consent: GoogleUmp", BuildOutlook.MissingConsent(_settings, true, false), "Google's form is not the game's flow");
            Assert.IsNull(BuildOutlook.MissingConsent(_settings, false, true));

            _settings.Consent = ConsentSource.None;
            Assert.IsNull(BuildOutlook.MissingConsent(_settings, false, false), "None names no flow to wait for; the validator fails it in a release build");
            _settings.SelectedMediation = AdProviderKind.AdMob;
            _settings.Consent = ConsentSource.GameFlow;
            Assert.IsNull(BuildOutlook.MissingConsent(_settings, false, false), "the AdMob provider runs Google's form itself");
        }

        // Issue #181: Project Settings and the debug panel said "LevelPlay live ads" while the runtime logged "no ads run:", because the
        // bootstrap decided the missing consent flow after BuildOutlook
        [Test]
        public void TheReleaseRow_SaysNoAdsRun_WhenTheSettingsNameGooglesFormWithoutItsPackages()
        {
            FillLevelPlay();
            _settings.Consent = ConsentSource.GoogleUmp;
            var release = Find(Rows(googleConsent: false), "Release build");
            Assert.AreEqual(HostMode.Idle, release.Mode);
            Assert.IsTrue(release.Problem);
            StringAssert.StartsWith("no ads run: the settings name Google's consent form (UMP) before LevelPlay", release.Text);
            StringAssert.Contains("validator stops this build", release.Text);
        }

        private void FillLevelPlay()
        {
            foreach (var tab in new[] { _settings.Test, _settings.Release })
            {
                tab.LevelPlay.Android.AppKey = "key";
                tab.LevelPlay.Android.RewardedUnitId = "rewarded";
            }
        }

        private IReadOnlyList<OutlookRow> Rows(bool installed = true, bool googleConsent = true)
        {
            var status = ProviderStatusResolver.Resolve(_settings, _settings.SelectedMediation, AdPlatform.Android, installed);
            return BuildOutlook.Rows(_settings, AdPlatform.Android, installed, status, googleConsent);
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

        // issue #133: the bootstrap read the advertising id before every start; only the test-device comparison uses it
        [Test]
        public void TheAdvertisingId_IsReadOnlyWhereTheGateComparesIt_AndNoMediationNamesNoDeviceRule()
        {
            bool Reads(BuildKind build) => BuildOutlook.ReadsAdvertisingId(_settings, build, _settings.ToProviderConfig(build, AdPlatform.Android, _settings.SelectedMediation));
            var levelPlay = (Reads(BuildKind.Development), Reads(BuildKind.Editor), Reads(BuildKind.Release));
            _settings.SelectedMediation = AdProviderKind.None;
            var none = BuildOutlook.Decide(_settings, BuildKind.Development, _settings.ToProviderConfig(BuildKind.Development, AdPlatform.Android, AdProviderKind.None), null, false, false, false);

            Assert.That((levelPlay, Reads(BuildKind.Development), none.Mode, none.Gate.Rule),
                Is.EqualTo(((true, false, false), false, HostMode.Simulator, (string)null)));
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
