using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Tests
{
    /// <summary>A controllable project for the validator; nothing here touches the development project.</summary>
    public sealed class FakeProbe : IProjectProbe
    {
        public readonly HashSet<AdProviderKind> Sdks = new HashSet<AdProviderKind>();
        public readonly HashSet<AdProviderKind> Upm = new HashSet<AdProviderKind>();
        public readonly HashSet<AdProviderKind> ProviderPackages = new HashSet<AdProviderKind>();
        public readonly List<string> Networks = new List<string>();
        public readonly List<string> LinkXml = new List<string>();
        public bool Edm = true;
        public string Manifest = "{ \"dependencies\": {} }";
        public string AdMobAppId;
        public int TargetSdk = 34, MinSdk = 23;
        public string AndroidManifest;

        public bool SdkInstalled(AdProviderKind kind) => Sdks.Contains(kind);
        public bool SdkViaUpm(AdProviderKind kind) => Upm.Contains(kind);
        public bool ProviderPackageInstalled(AdProviderKind kind) => ProviderPackages.Contains(kind);
        public IReadOnlyList<string> DetectedNetworks => Networks;
        public bool ExternalDependencyManagerInstalled => Edm;
        public string CustomAndroidManifest => AndroidManifest;
        public int AndroidTargetSdk => TargetSdk;
        public int AndroidMinSdk => MinSdk;
        public IReadOnlyList<string> LinkXmlTexts => LinkXml;
        public string ConsumerManifestJson => Manifest;
        public string AdMobSettingsAppId(AdPlatform platform) => AdMobAppId;

        public FakeProbe Installed(AdProviderKind kind, bool viaUpm = true)
        {
            Sdks.Add(kind); ProviderPackages.Add(kind); if (viaUpm) Upm.Add(kind);
            return this;
        }
    }

    public sealed class AdsValidatorTests
    {
        private AdsSettings _settings;
        private PlatformCatalog _catalog;
        private FakeProbe _probe;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.PrivacyPolicyUrl = "https://example.com/privacy";
            _settings.Release.LevelPlay.Android.AppKey = "live";
            _settings.Release.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _settings.Test.LevelPlay.Android.AppKey = "live";
            _settings.Test.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _settings.TestDeviceIds.Add("gaid");
            _catalog = PlatformCatalog.Load();
            _probe = new FakeProbe().Installed(AdProviderKind.LevelPlay);
            _probe.LinkXml.Add("<linker><assembly fullname=\"Nexus.Ads.Core\" preserve=\"all\"/></linker>");
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        private AdsValidationReport Run(AdsValidator.Tab tab) => AdsValidator.Validate(_settings, tab, AdPlatform.Android, _probe, _catalog);

        private static AdsCheck Check(AdsValidationReport report, string id) => report.checks.First(c => c.id == id);

        [Test]
        public void ACompleteProject_PassesBothTabs()
        {
            var release = Run(AdsValidator.Tab.Release);
            var test = Run(AdsValidator.Tab.Test);
            Assert.AreEqual("pass", release.outcome, release.Summary());
            Assert.AreEqual("pass", test.outcome, test.Summary());
            Assert.AreEqual("Active", release.status);
        }

        [Test]
        public void SelectedMediation_NotActive_FailsTheReleaseTab_UnlessAdFreeRelease()
        {
            _settings.Release.LevelPlay.Android.RewardedUnitId = "";
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Fail, Check(report, "mediation.status").Result);
            StringAssert.Contains("rewarded unit id", Check(report, "mediation.status").fix);
            _settings.AdFreeRelease = true;
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "mediation.status").Result);
        }

        [Test]
        public void TwoMediationSdks_Fail_AndTheStatusSaysInvalid()
        {
            _probe.Installed(AdProviderKind.Max);
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Fail, Check(report, "mediation.single").Result);
            Assert.AreEqual("Invalid", report.status, "the badge and the check say the same thing");
        }

        [Test]
        public void GoogleSdk_NextToTheSelectedMediation_Warns_AndIsNotASecondMediation()
        {
            _probe.Installed(AdProviderKind.AdMob);
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Warn, Check(report, "mediation.admob").Result);
            Assert.AreEqual(CheckResult.Pass, Check(report, "mediation.single").Result, "AdMob is not counted as a second mediation");
            Assert.AreEqual("Active", report.status);
        }

        [Test]
        public void GoogleSampleIds_OnTheReleaseTab_Fail()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            _probe.Installed(AdProviderKind.AdMob);
            _probe.AdMobAppId = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Release.AdMob.Android.AppKey = AdMobSampleIds.PublisherPrefix + "~3347511713";
            _settings.Release.AdMob.Android.RewardedUnitId = AdMobSampleIds.PublisherPrefix + "/5224354917";
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Fail, Check(report, "keys.release.testdata").Result);
        }

        [Test]
        public void AdMobTestTab_MustEqualTheCatalogSampleIds()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            _probe.Installed(AdProviderKind.AdMob);
            var sample = _catalog.SampleAdMobKeys(AdPlatform.Android);
            _settings.Test.AdMob.Android.AppKey = sample.AppKey;
            _settings.Test.AdMob.Android.RewardedUnitId = "ca-app-pub-1234567890123456/1234567890";
            _probe.AdMobAppId = sample.AppKey;
            var report = Run(AdsValidator.Tab.Test);
            Assert.AreEqual(CheckResult.Fail, Check(report, "keys.test.official").Result);
            _settings.Test.AdMob.Android.RewardedUnitId = sample.RewardedUnitId;
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Test), "keys.test.official").Result);
        }

        [Test]
        public void LevelPlayTestTab_MustMirrorTheReleaseTab_WhenReleaseIsFilled()
        {
            _settings.Test.LevelPlay.Android.AppKey = "other";
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Test), "keys.test.mirror").Result);
            _settings.Release.LevelPlay.Android.AppKey = "";
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Test), "keys.test.mirror").Result, "nothing to mirror while Release is empty");
        }

        [Test]
        public void EmptyDeviceList_Warns()
        {
            _settings.TestDeviceIds.Clear();
            var check = Check(Run(AdsValidator.Tab.Test), "devices.empty");
            Assert.AreEqual(CheckResult.Warn, check.Result);
            StringAssert.Contains("simulator", check.message);
        }

        [Test]
        public void ChildDirected_WithMax_Fails_AndOffListNetworks_Warn()
        {
            _settings.ChildDirected = true;
            _settings.SelectedMediation = AdProviderKind.Max;
            _probe.Sdks.Clear(); _probe.ProviderPackages.Clear(); _probe.Upm.Clear();
            _probe.Installed(AdProviderKind.Max);
            _probe.Networks.AddRange(new[] { "pangle", "inmobi" });
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Fail, Check(report, "child.mediation").Result);
            var networks = Check(report, "child.networks");
            Assert.AreEqual(CheckResult.Warn, networks.Result);
            StringAssert.Contains("Pangle", networks.message);
            StringAssert.DoesNotContain("InMobi", networks.message, "InMobi is on Google's list");
        }

        [Test]
        public void UnityPackageImport_Warns_WithTheDefineToSet()
        {
            _probe.Upm.Clear();
            var check = Check(Run(AdsValidator.Tab.Test), "sdk.unitypackage");
            Assert.AreEqual(CheckResult.Warn, check.Result);
            StringAssert.Contains("NEXUS_LEVELPLAY", check.message);
        }

        [Test]
        public void MissingEdm_FailsRelease_NotTest()
        {
            _probe.Edm = false;
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Release), "android.edm").Result);
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Test), "android.edm").Result);
        }

        [Test]
        public void PlacementNames_MustBeIdentifiers_AndInterstitialsNaturalBreaks()
        {
            _settings.Placements.Add(new PlacementEntry { Name = "level-end", Format = AdFormat.Interstitial, NaturalBreak = true });
            _settings.Placements.Add(new PlacementEntry { Name = "mid_game", Format = AdFormat.Interstitial, NaturalBreak = false });
            var check = Check(Run(AdsValidator.Tab.Test), "placements.names");
            Assert.AreEqual(CheckResult.Fail, check.Result);
            StringAssert.Contains("level-end", check.message);
            StringAssert.Contains("mid_game", check.message);
        }

        [Test]
        public void FloorsBelowDefault_PrivacyUrlEmpty_MissingLinkXml_Warn()
        {
            _settings.Policy.MinInterstitialGapSeconds = 5;
            _settings.PrivacyPolicyUrl = "";
            _probe.LinkXml.Clear();
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Warn, Check(report, "policy.floors").Result);
            Assert.AreEqual(CheckResult.Warn, Check(report, "privacy.url").Result);
            Assert.AreEqual(CheckResult.Warn, Check(report, "android.linkxml").Result);
        }

        [Test]
        public void CustomManifest_WithoutAdId_OnTarget33_Warns()
        {
            _probe.AndroidManifest = "<manifest/>";
            Assert.AreEqual(CheckResult.Warn, Check(Run(AdsValidator.Tab.Release), "android.adid").Result);
            _probe.AndroidManifest = "<manifest><uses-permission android:name=\"com.google.android.gms.permission.AD_ID\"/></manifest>";
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "android.adid").Result);
        }

        [Test]
        public void DetectedNetwork_WithoutAnAppAdsLine_Warns()
        {
            _probe.Networks.Add("meta");
            Assert.AreEqual(CheckResult.Warn, Check(Run(AdsValidator.Tab.Release), "appads.lines").Result);
            _settings.AppAdsTxtLines.Add("facebook.com, 123, DIRECT, c3e20eee3f780d68 # meta");
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "appads.lines").Result);
        }

        [Test]
        public void FamilyPackages_AtDifferentCommits_Fail()
        {
            _probe.Manifest = "{ \"dependencies\": { \"com.nexus.ads\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#aaaaaaa\", \"com.nexus.ads.levelplay\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.levelplay#bbbbbbb\" } }";
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Release), "manifest.pins").Result);
            _probe.Manifest = ManifestPinning.Pin(_probe.Manifest);
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "manifest.pins").Result);
        }

        [Test]
        public void Report_CountsAndOutcome_AreConsistent()
        {
            _settings.TestDeviceIds.Clear();
            var report = Run(AdsValidator.Tab.Test);
            Assert.AreEqual(report.checks.Count, report.pass + report.warn + report.fail);
            Assert.AreEqual("warn", report.outcome);
            Assert.IsTrue(report.Passed, "warnings do not fail");
            var json = JsonUtility.ToJson(report);
            var back = JsonUtility.FromJson<AdsValidationReport>(json);
            Assert.AreEqual(report.checks.Count, back.checks.Count);
            Assert.AreEqual("devices.empty", back.checks.First(c => c.result == "warn").id);
        }
    }

    public sealed class ProjectTextTests
    {
        [Test]
        public void Placements_RenderAsPascalConstants_WithTheRawNameAsValue()
        {
            string code = AdPlacementsGenerator.Render(new[] { new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded }, new PlacementEntry { Name = "level_end", Format = AdFormat.Interstitial, NaturalBreak = true } });
            StringAssert.Contains("public const string ShopRefill = \"shop_refill\";", code);
            StringAssert.Contains("public const string LevelEnd = \"level_end\";", code);
            StringAssert.Contains("namespace Nexus.Ads", code);
        }

        [Test]
        public void Placements_WithABadName_RefuseToRender()
        {
            Assert.Throws<System.InvalidOperationException>(() => AdPlacementsGenerator.Render(new[] { new PlacementEntry { Name = "level-end" } }));
            Assert.Throws<System.InvalidOperationException>(() => AdPlacementsGenerator.Render(new[] { new PlacementEntry { Name = "a" }, new PlacementEntry { Name = "a" } }));
        }

        [Test]
        public void AppAdsTxt_JoinsTrimmedUniqueLines()
        {
            string text = AppAdsTxt.Render(new[] { " a.com, 1, DIRECT ", "", "a.com, 1, DIRECT", "b.com, 2, RESELLER" });
            Assert.AreEqual(2, text.Split('\n').Count(l => l.Length > 0 && !l.StartsWith("#")));
        }

        [Test]
        public void ManifestPinning_RewritesEveryFamilyEntryToTheCoreCommit_AndLeavesOthers()
        {
            string manifest = "{ \"dependencies\": { \"com.nexus.core\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#1111111\", \"com.nexus.ads\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#aaaaaaa\", \"com.nexus.ads.bridge.nexus\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads.bridge.nexus#bbbbbbb\" } }";
            string pinned = ManifestPinning.Pin(manifest);
            StringAssert.Contains("com.nexus.ads.bridge.nexus#aaaaaaa", pinned);
            StringAssert.Contains("com.nexus.core#1111111", pinned, "the core is not part of the family");
            Assert.IsNull(ManifestPinning.Fault(pinned));
            Assert.AreEqual("{}", ManifestPinning.Pin("{}"), "an embedded project is untouched");
        }

        [Test]
        public void LinkXml_PreservesTheCoreAndTheSelectedProvider()
        {
            string xml = LinkXml.Render(AdProviderKind.LevelPlay);
            StringAssert.Contains("Nexus.Ads.Core", xml);
            StringAssert.Contains("Nexus.Ads.LevelPlay", xml);
            StringAssert.DoesNotContain("Nexus.Ads.Max", xml);
        }

        [Test]
        public void Catalog_ParsesAndNamesTheThreeProviders()
        {
            var catalog = PlatformCatalog.Load(fresh: true);
            Assert.AreEqual(3, catalog.providers.Length);
            Assert.AreEqual("com.unity.services.levelplay", catalog.Provider(AdProviderKind.LevelPlay).sdk.upm.package);
            Assert.AreEqual(AdMobSampleIds.PublisherPrefix, catalog.Provider(AdProviderKind.AdMob).testData.publisherPrefix);
            Assert.IsFalse(catalog.Provider(AdProviderKind.Max).childDirectedAllowed);
            Assert.IsTrue(catalog.Network("inmobi").childDirectedAllowed);
            Assert.IsFalse(catalog.Network("chartboost").childDirectedAllowed);
        }
    }
}
