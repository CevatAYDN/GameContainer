using System;
using System.Linq;
using NUnit.Framework;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Tests
{
    /// <summary>
    /// Prepare Android: the plan per mediation (what a fresh project misses, what is never automated), the
    /// validator stopping a build the plan says will fail in Gradle, and the text edits it makes (idempotent).
    /// </summary>
    public sealed class AndroidSetupTests
    {
        private const string GitManifest = "{\n  \"dependencies\": {\n    \"com.nexus.ads\": \"https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.ads#eee5c2f5ddffb90c6100871a1971cccfd310c1bc\"\n  }\n}\n";
        private const string VersionsJson = "{ \"unityPackage\": { \"versions\": { \"9.5.1\": { \"ironSourceSdkVersion\": \"[9.5.0, 10.0[\" }, \"9.4.0\": { \"ironSourceSdkVersion\": \"[9.4.0, 10.0[\" } } }, \"IronSourceSdk\": { \"dependencyXmlURL\": \"https://s3.amazonaws.com/ssa.public/Dependencies-xmls/IronSource/${VERSION}/IronSourceSDKDependencies.xml\" } }";

        private AdsSettings _settings;
        private PlatformCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<AdsSettings>();
            _settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
            _settings.Release.LevelPlay.Android.AppKey = "live";
            _settings.Release.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _settings.Test.LevelPlay.Android.AppKey = "live";
            _settings.Test.LevelPlay.Android.RewardedUnitId = "live-rewarded";
            _settings.TestDeviceIds.Add("gaid");
            _catalog = PlatformCatalog.Load();
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_settings);

        private static SetupStatus Status(System.Collections.Generic.List<SetupStep> plan, string id) => plan.First(s => s.id == id).status;

        [Test]
        public void LevelPlay_FreshProject_EveryAndroidStepIsPending_AndTheValidatorStopsTheBuild_UntilPrepared()
        {
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var probe = new FakeProbe().Installed(AdProviderKind.LevelPlay);
            probe.Edm = false;
            probe.Packages["com.unity.services.levelplay"] = "9.5.1";
            probe.PackageFiles["com.unity.services.levelplay/Editor/Json/LevelPlayVersions.json"] = VersionsJson;

            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog);
            Assert.AreEqual(SetupStatus.Done, Status(plan, "sdk"));
            foreach (var id in new[] { "resolver", "native.sdk", "gradle.template", "gradle.lines", "resolve" })
                Assert.AreEqual(SetupStatus.Pending, Status(plan, id), id);
            StringAssert.Contains("IronSource/9.5.0/IronSourceSDKDependencies.xml", plan.First(s => s.id == "native.sdk").how, "the native SDK the installed package pairs with");
            var report = AdsValidator.Validate(_settings, AdsValidator.Tab.Test, AdPlatform.Android, probe, _catalog);
            Assert.AreEqual(CheckResult.Fail, report.checks.First(c => c.id == "android.nativesdk").Result);
            Assert.AreEqual(CheckResult.Fail, report.checks.First(c => c.id == "android.gradle").Result);

            probe.Edm = true;
            probe.AndroidPrepared();
            Assert.IsTrue(AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog).All(s => s.status == SetupStatus.Done));
            report = AdsValidator.Validate(_settings, AdsValidator.Tab.Test, AdPlatform.Android, probe, _catalog);
            Assert.AreEqual(CheckResult.Pass, report.checks.First(c => c.id == "android.nativesdk").Result);
            Assert.AreEqual(CheckResult.Pass, report.checks.First(c => c.id == "android.gradle").Result);
        }

        [Test]
        public void AdMob_NeedsOnlyItsAppId_FromTheTabBeingBuilt()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            var sample = _catalog.SampleAdMobKeys(AdPlatform.Android);
            _settings.Test.AdMob.Android.AppKey = sample.AppKey;
            var probe = new FakeProbe().Installed(AdProviderKind.AdMob);
            probe.AdMobAppId = "";

            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog);
            CollectionAssert.AreEquivalent(new[] { "sdk", "provider", "admob.appid" }, plan.Select(s => s.id).ToArray(), "the plugin brings its resolver and needs no Gradle lines");
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "admob.appid"));
            Assert.AreEqual(SetupStatus.Manual, Status(AndroidSetup.Plan(_settings, AdsValidator.Tab.Release, probe, _catalog), "admob.appid"), "an empty Release tab is the developer's to fill");
            probe.AdMobAppId = sample.AppKey;
            Assert.AreEqual(SetupStatus.Done, Status(AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog), "admob.appid"));
        }

        [Test]
        public void Max_IsManualOnly_UntilItsProviderExists()
        {
            _settings.SelectedMediation = AdProviderKind.Max;
            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, new FakeProbe(), _catalog);
            Assert.IsNotEmpty(plan);
            Assert.IsTrue(plan.All(s => s.status == SetupStatus.Manual), string.Join("\n", plan));
        }

        [Test]
        public void ProviderPackage_IsAddedAtTheCoreCommit_OrLeftManual_WhenTheCoreIsNotAGitPin()
        {
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var probe = new FakeProbe { Manifest = GitManifest };
            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog);
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "provider"));
            StringAssert.Contains("path=/Nexus/Packages/com.nexus.ads.levelplay#eee5c2f5ddffb90c6100871a1971cccfd310c1bc", plan.First(s => s.id == "provider").how);
            probe.Manifest = "{ \"dependencies\": { \"com.nexus.ads\": \"file:../GameContainer/Nexus/Packages/com.nexus.ads\" } }";
            Assert.AreEqual(SetupStatus.Manual, Status(AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog), "provider"));
        }

        [Test]
        public void AdMobProvider_BringsTheCoreItStillDeclares_InAGameWithoutIt()
        {
            _settings.SelectedMediation = AdProviderKind.AdMob;
            var probe = new FakeProbe { Manifest = GitManifest };
            string how = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog).First(s => s.id == "provider").how;
            StringAssert.Contains("path=/Nexus/Packages/com.nexus.ads.admob#eee5c2f5ddffb90c6100871a1971cccfd310c1bc", how);
            StringAssert.Contains("path=/Nexus/Packages/com.nexus.core#eee5c2f5ddffb90c6100871a1971cccfd310c1bc", how, "UPM does not resolve the git dependency by itself");
            probe.Packages["com.nexus.core"] = "0.5.0";
            StringAssert.DoesNotContain("com.nexus.core", AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog).First(s => s.id == "provider").how);
        }

        [Test]
        public void NativeSdk_IsTheLowerBoundOfTheInstalledPackagesRange()
        {
            Assert.IsTrue(AndroidSetup.TryNativeSdk(VersionsJson, "9.4.0", out var version, out var url));
            Assert.AreEqual("9.4.0", version);
            Assert.AreEqual("https://s3.amazonaws.com/ssa.public/Dependencies-xmls/IronSource/9.4.0/IronSourceSDKDependencies.xml", url);
            Assert.IsFalse(AndroidSetup.TryNativeSdk(VersionsJson, "8.0.0", out _, out _), "a package version the file does not list is not guessed");
        }

        [Test]
        public void GradleLines_GoInsideDependencies_Once_KeepingLineEndings()
        {
            string template = "apply plugin: 'com.android.library'\r\ndependencies {\r\n    implementation fileTree(dir: 'libs', include: ['*.jar'])\r\n**DEPS**}\r\n";
            string once = AndroidSetup.WithGradleLines(template, new[] { "implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')" }, "why");
            StringAssert.Contains("dependencies {\r\n    // Nexus Ads: why\r\n    implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')\r\n", once);
            Assert.AreEqual(once, AndroidSetup.WithGradleLines(once, new[] { "implementation platform('org.jetbrains.kotlin:kotlin-bom:1.8.22')" }, "why"), "a second press changes nothing");
            Assert.IsFalse(once.Replace("\r\n", "").Contains("\n"), "no bare LF in a CRLF template");
        }

        [Serializable] private sealed class Registry { public string name; public string url; public string[] scopes; }
        [Serializable] private sealed class ManifestRegistries { public Registry[] scopedRegistries; }

        [Test]
        public void OpenUpmScopes_AddTheRegistry_OrExtendIt_WithoutDuplicates()
        {
            string added = AndroidSetup.WithOpenUpmScopes(GitManifest, new[] { "com.google.external-dependency-manager" });
            var parsed = JsonUtility.FromJson<ManifestRegistries>(added);
            Assert.AreEqual(1, parsed.scopedRegistries.Length, added);
            CollectionAssert.AreEqual(new[] { "com.google.external-dependency-manager" }, parsed.scopedRegistries[0].scopes);
            StringAssert.Contains("com.nexus.ads", added, "dependencies kept");

            string extended = AndroidSetup.WithOpenUpmScopes(added, new[] { "com.google.ads.mobile", "com.google.external-dependency-manager" });
            CollectionAssert.AreEqual(new[] { "com.google.external-dependency-manager", "com.google.ads.mobile" }, JsonUtility.FromJson<ManifestRegistries>(extended).scopedRegistries[0].scopes);

            string broad = AndroidSetup.WithOpenUpmScopes(GitManifest, new[] { "com.google" });
            Assert.AreEqual(broad, AndroidSetup.WithOpenUpmScopes(broad, new[] { "com.google.ads.mobile" }), "a broader scope already covers the package");
        }
    }
}
