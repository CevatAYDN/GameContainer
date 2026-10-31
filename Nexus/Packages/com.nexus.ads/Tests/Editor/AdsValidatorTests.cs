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
        public bool EdmStub;
        public string Manifest = "{ \"dependencies\": {} }";
        public string AdMobAppId;
        public int TargetSdk = 34, MinSdk = 23;
        public string AndroidManifest;
        public readonly Dictionary<string, string> Packages = new Dictionary<string, string>();
        public readonly Dictionary<string, string> PackageFiles = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>();

        public bool SdkInstalled(AdProviderKind kind) => Sdks.Contains(kind);
        public bool SdkViaUpm(AdProviderKind kind) => Upm.Contains(kind);
        public bool ProviderPackageInstalled(AdProviderKind kind) => ProviderPackages.Contains(kind);
        public IReadOnlyList<string> DetectedNetworks => Networks;
        public bool ExternalDependencyManagerInstalled => Edm;
        public bool DisabledEdmStub => EdmStub;
        public string CustomAndroidManifest => AndroidManifest;
        public int AndroidTargetSdk => TargetSdk;
        public int AndroidMinSdk => MinSdk;
        public IReadOnlyList<string> LinkXmlTexts => LinkXml;
        public string ConsumerManifestJson => Manifest;
        public string AdMobSettingsAppId(AdPlatform platform) => AdMobAppId;
        public string PackageVersion(string package) => package != null && Packages.TryGetValue(package, out var v) ? v : null;
        public string PackageFileText(string package, string relativePath) => PackageFiles.TryGetValue(package + "/" + relativePath, out var t) ? t : null;
        public string ProjectFileText(string path) => path != null && Files.TryGetValue(path, out var t) ? t : null;

        public FakeProbe Installed(AdProviderKind kind, bool viaUpm = true)
        {
            Sdks.Add(kind); ProviderPackages.Add(kind); if (viaUpm) Upm.Add(kind);
            Packages[kind == AdProviderKind.LevelPlay ? "com.nexus.ads.levelplay" : kind == AdProviderKind.AdMob ? "com.nexus.ads.admob" : "com.nexus.ads.max"] = "0.1.0";
            return this;
        }

        public const string LevelPlayPackage = "com.unity.services.levelplay";
        public const string PackageVersionsFile = LevelPlayPackage + "/Editor/Json/LevelPlayVersions.json";
        public const string ProjectVersionsFile = "Assets/LevelPlay/Editor/LevelPlayVersions.json";
        public const string NativeFile = "Assets/LevelPlay/Editor/IronSourceSDKDependencies.xml";
        public const string AdapterFile = "Assets/LevelPlay/Editor/ISUnityAdsAdapterDependencies.xml";

        /// <summary>
        /// LevelPlayVersions.json in the shape LevelPlay ships it. As in the real file of package 9.6.0, the Unity Ads adapter
        /// 5.16.0.0 declares the Android adapter 5.15.0.
        /// </summary>
        public const string LevelPlayVersions = "{ \"unityPackage\": { \"versions\": { \"9.6.0\": { \"ironSourceSdkVersion\": \"[9.6.0, 10.0[\" }, \"9.5.1\": { \"ironSourceSdkVersion\": \"[9.5.0, 10.0[\" }, "
            + "\"9.4.0\": { \"ironSourceSdkVersion\": \"[9.4.0, 9.5.0[\" }, \"9.3.0\": { \"ironSourceSdkVersion\": \"9.3.2\" }, \"9.2.0\": { \"ironSourceSdkVersion\": \"[9.2.0, 9.3.0[\" } } }, "
            + "\"IronSourceSdk\": { \"dependencyXmlURL\": \"https://s3.amazonaws.com/ssa.public/Dependencies-xmls/IronSource/${VERSION}/IronSourceSDKDependencies.xml\", \"versions\": { "
            + "\"9.7.0\": { \"androidSdkVersion\": \"9.7.0\" }, \"9.6.0\": { \"androidSdkVersion\": \"9.6.0\" }, \"9.5.0\": { \"androidSdkVersion\": \"9.5.0\", \"message\": \"a brace in text { is no object\" }, "
            + "\"9.4.5\": { \"androidSdkVersion\": \"9.4.5\" }, \"9.4.0\": { \"androidSdkVersion\": \"9.4.0\" }, \"9.3.2\": { \"androidSdkVersion\": \"9.3.2\" }, \"9.3.1\": { \"androidSdkVersion\": \"9.3.1\" } } }, "
            + "\"adapters\": { \"AdMob\": { \"dependencyXmlURL\": \"https://s3.amazonaws.com/ssa.public/Dependencies-xmls/AdMob/${VERSION}/ISAdMobAdapterDependencies.xml\", \"versions\": { \"9.9.9.0\": { \"ironSourceSdkVersion\": \"[9.0.0, 10.0[\" } } }, "
            + "\"UnityAds\": { \"dependencyXmlURL\": \"https://s3.amazonaws.com/ssa.public/Dependencies-xmls/UnityAds/${VERSION}/ISUnityAdsAdapterDependencies.xml\", \"versions\": { "
            + "\"5.16.0.0\": { \"ironSourceSdkVersion\": \"[9.6.0, 10.0[\", \"android\": { \"adapterVersion\": \"5.15.0\" } }, "
            + "\"5.11.0.0\": { \"ironSourceSdkVersion\": \"[9.0.0, 10.0[\", \"android\": { \"adapterVersion\": \"5.11.0\" } }, "
            + "\"5.9.0.0\": { \"ironSourceSdkVersion\": \"[9.0.0, 10.0[\" }, "
            + "\"4.3.49.0\": { \"ironSourceSdkVersion\": \"[8.4.0, 9.0.0[\" } } } } }";

        /// <summary>A dependency file as LevelPlay's servers publish it: its own version, and the Android spec it declares.</summary>
        public static string DependencyXml(string unityVersion, string spec)
            => "<dependencies>\n  <unityversion>" + unityVersion + "</unityversion>\n  <androidPackages>\n    <androidPackage spec=\"" + spec + "\">\n    </androidPackage>\n  </androidPackages>\n</dependencies>\n";

        /// <summary>A main template the resolver wrote for these specs, with the Kotlin alignment.</summary>
        public static string Template(params string[] specs)
            => "dependencies {\n    implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')\n" + string.Concat(specs.Select(s => "    implementation '" + s + "' // resolved\n")) + "}\n";

        /// <summary>
        /// What Prepare Android leaves behind for LevelPlay <paramref name="package"/> with <see cref="LevelPlayVersions"/>:
        /// the newest native SDK its range allows (9.7.0), the Unity Ads adapter for it (5.16.0.0, Android 5.15.0), and a
        /// template resolved to both.
        /// </summary>
        public FakeProbe AndroidPrepared(string package = "9.5.1")
        {
            Packages[LevelPlayPackage] = package;
            PackageFiles[PackageVersionsFile] = LevelPlayVersions;
            Files[NativeFile] = DependencyXml("9.7.0", "com.unity3d.ads-mediation:mediation-sdk:9.7.0");
            Files[AdapterFile] = DependencyXml("5.16.0.0", "com.unity3d.ads-mediation:unityads-adapter:5.15.0");
            Files[AndroidSetup.MainTemplatePath] = Template("com.unity3d.ads-mediation:mediation-sdk:9.7.0", "com.unity3d.ads-mediation:unityads-adapter:5.15.0");
            Files["Assets/Plugins/Android/gradleTemplate.properties"] = "org.gradle.parallel=true\n**ADDITIONAL_PROPERTIES**\n";
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
            _probe = new FakeProbe().Installed(AdProviderKind.LevelPlay).AndroidPrepared();
            _probe.LinkXml.Add("<linker><assembly fullname=\"Nexus.Ads.Core\" preserve=\"all\"/></linker>");
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_settings);

        private AdsValidationReport Run(AdsValidator.Tab tab) => AdsValidator.Validate(_settings, tab, AdPlatform.Android, _probe, _catalog);

        private static AdsCheck Check(AdsValidationReport report, string id) => report.checks.First(c => c.id == id);

        [Test]
        public void ACompleteProject_PassesBothTabs()
        {
            WithGoogleConsent("ca-app-pub-1111111111111111~2222222222");
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

        /// <summary>Google Mobile Ads and com.nexus.ads.admob in the project, the Google settings asset carrying <paramref name="appId"/>.</summary>
        private void WithGoogleConsent(string appId)
        {
            _probe.Installed(AdProviderKind.AdMob);
            _probe.AdMobAppId = appId;
        }

        [Test]
        public void GoogleSdk_NextToTheSelectedMediation_WithoutItsPackage_Warns_AndIsNotASecondMediation()
        {
            _probe.Sdks.Add(AdProviderKind.AdMob);
            _probe.Upm.Add(AdProviderKind.AdMob);
            _probe.AdMobAppId = "ca-app-pub-1111111111111111~2222222222";
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Warn, Check(report, "mediation.admob").Result);
            StringAssert.Contains("com.nexus.ads.admob", Check(report, "mediation.admob").fix, "the fix names the package that brings Google's consent form");
            Assert.AreEqual(CheckResult.Pass, Check(report, "mediation.single").Result, "AdMob is not counted as a second mediation");
            Assert.AreEqual("Active", report.status);
        }

        [Test]
        public void LevelPlay_RunsGooglesConsentForm_WhenItsPackagesAreIn_AndWarnsWithoutAnyConsentPlatform()
        {
            // issue #63: LevelPlay ships no consent form
            var bare = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Warn, Check(bare, "consent.form").Result);
            StringAssert.Contains("SetGdprConsent", Check(bare, "consent.form").fix, "the way out for a game with its own consent platform");

            WithGoogleConsent("ca-app-pub-1111111111111111~2222222222");
            var report = Run(AdsValidator.Tab.Release);
            Assert.AreEqual(CheckResult.Pass, Check(report, "consent.form").Result);
            Assert.AreEqual(CheckResult.Pass, Check(report, "mediation.admob").Result);
        }

        [Test]
        public void GoogleSdk_BesideLevelPlay_NeedsAnAppId_AndNoSampleOneInARelease()
        {
            // issue #63: the Google Mobile Ads Android SDK stops the app at launch without an App ID, whichever mediation runs
            WithGoogleConsent(null);
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Test), "android.admob.appid").Result, "no settings asset: every build stops at launch");
            _probe.AdMobAppId = "";
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Release), "android.admob.appid").Result);

            _probe.AdMobAppId = AdMobSampleIds.PublisherPrefix + "~3347511713";
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Test), "android.admob.appid").Result, "Google's sample App ID serves a development build");
            Assert.AreEqual(CheckResult.Fail, Check(Run(AdsValidator.Tab.Release), "android.admob.appid").Result, "a release would show Google's test message");

            _probe.AdMobAppId = "ca-app-pub-1111111111111111~2222222222";
            Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "android.admob.appid").Result);
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
        public void DetectedNetwork_IsCoveredByItsAdSystemDomainLine_NotByItsNameInAComment()
        {
            // One line per network in the form its own app-ads.txt page gives (issue #124); Unity's is Wickling's hosted line.
            var lines = new[]
            {
                ("unityads", "unity.com, 18717623, DIRECT, 96cabb5fbdde37a7"), ("unityads", "unity3d.com, 123, DIRECT, 96cabb5fbdde37a7"),
                ("admob", "google.com, pub-0000000000000000, DIRECT, f08c47fec0942fa0"), ("mintegral", "mintegral.com, 123, DIRECT, 0aeed750c80d6423"),
                ("pangle", "pangleglobal.com, 1234, DIRECT"), ("chartboost", "chartboost.com,123,DIRECT"),
                ("meta", "facebook.com, 123, RESELLER, c3e20eee3f780d68"), ("inmobi", "inmobi.com,74dcd2c4fa2d401cb74bbb9c124a268c,DIRECT,83e75a7ae333ca9d"),
                ("liftoff", "vungle.com, 123, DIRECT, c107d686becd2d77"), ("dtexchange", "FYBER.COM,123,DIRECT,1ad675c9de6b5176"),
            };
            foreach (var (network, line) in lines)
            {
                _probe.Networks.Clear();
                _probe.Networks.Add(network);
                _settings.AppAdsTxtLines.Clear();
                Assert.AreEqual(CheckResult.Warn, Check(Run(AdsValidator.Tab.Release), "appads.lines").Result, network + " without a line");
                _settings.AppAdsTxtLines.Add("ironsrc.com, 1234, DIRECT # " + network);
                Assert.AreEqual(CheckResult.Warn, Check(Run(AdsValidator.Tab.Release), "appads.lines").Result, network + " named only in a comment");
                _settings.AppAdsTxtLines.Add(line);
                Assert.AreEqual(CheckResult.Pass, Check(Run(AdsValidator.Tab.Release), "appads.lines").Result, line);
            }
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
            WithGoogleConsent("ca-app-pub-1111111111111111~2222222222"); // the empty device list is then the only warning
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
