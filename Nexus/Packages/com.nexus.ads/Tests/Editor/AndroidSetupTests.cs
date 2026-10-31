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
        private const string VersionsJson = FakeProbe.LevelPlayVersions;

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
            // issue #49: without gradle.properties EDM4U reports Resolution Failed on every resolution and build
            foreach (var id in new[] { "resolver", "native.sdk", "native.adapter", "gradle.template", "gradle.properties", "gradle.lines", "resolve" })
                Assert.AreEqual(SetupStatus.Pending, Status(plan, id), id);
            StringAssert.Contains("IronSource/9.7.0/IronSourceSDKDependencies.xml", plan.First(s => s.id == "native.sdk").how, "the newest native SDK the installed package allows");
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
        public void ADisabledEdmStub_IsNoResolver_AndPrepareAndroidAddsUnitysResolverInstead()
        {
            // issue #61: the three ad games embed a disabled com.google.external-dependency-manager beside Firebase
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-edm-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                Assert.IsFalse(UnityProjectProbe.IsDisabledEdmStub(root), "a package folder without the marker is a real package");
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".edm_dummy"), "");
                Assert.IsTrue(UnityProjectProbe.IsDisabledEdmStub(root));
            }
            finally { System.IO.Directory.Delete(root, true); }

            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var stubbed = new FakeProbe().Installed(AdProviderKind.LevelPlay);
            stubbed.Edm = false;
            stubbed.EdmStub = true;
            var step = AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, stubbed, _catalog).First(s => s.id == "resolver");
            Assert.AreEqual(SetupStatus.Pending, step.status);
            StringAssert.Contains(AndroidSetup.UnityEdmPackage, step.how);
            Assert.AreEqual(AndroidSetup.UnityEdmPackage, AndroidSetup.ResolverPackage(stubbed), "the embedded stub would shadow Google's package from OpenUPM");
            Assert.AreEqual(AndroidSetup.GoogleEdmPackage, AndroidSetup.ResolverPackage(new FakeProbe { Edm = false }));
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
        public void NativeSdk_IsTheNewestVersionTheInstalledPackageAllows_AsLevelPlaysOwnInstallerTakesIt()
        {
            // issue #144: Prepare Android took the lower bound (9.6.0 for package 9.6.0) where LevelPlay's installer takes 9.7.0
            Assert.IsTrue(AndroidSetup.TryNativeSdk(VersionsJson, "9.6.0", out var version, out var url));
            Assert.AreEqual("9.7.0", version);
            Assert.AreEqual("https://s3.amazonaws.com/ssa.public/Dependencies-xmls/IronSource/9.7.0/IronSourceSDKDependencies.xml", url);
            Assert.IsTrue(AndroidSetup.TryNativeSdk(VersionsJson, "9.4.0", out version, out _));
            Assert.AreEqual("9.4.5", version, "the range's upper bound is excluded");
            Assert.IsTrue(AndroidSetup.TryNativeSdk(VersionsJson, "9.3.0", out version, out _));
            Assert.AreEqual("9.3.2", version, "a version without brackets is that version only");
            Assert.IsFalse(AndroidSetup.TryNativeSdk(VersionsJson, "9.2.0", out _, out _), "a range the file lists no SDK in is not guessed");
            Assert.IsFalse(AndroidSetup.TryNativeSdk(VersionsJson, "8.0.0", out _, out _), "a package version the file does not list is not guessed");
        }

        [Test]
        public void AnUpgradedPackage_LeavesTheOldNativeDeclarationOpen_AndTheValidatorFails_UntilTheDeclarationAndTheTemplateNameTheNewVersion()
        {
            // issue #145: package 9.5.1 to 9.6.0 kept mediation-sdk 9.5.0 and every step read Done (ads-device-20261031)
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var probe = new FakeProbe().Installed(AdProviderKind.LevelPlay).AndroidPrepared("9.6.0");
            probe.Files[FakeProbe.NativeFile] = FakeProbe.DependencyXml("9.5.0", "com.unity3d.ads-mediation:mediation-sdk:9.5.0");
            probe.Files[FakeProbe.AdapterFile] = FakeProbe.DependencyXml("5.11.0.0", "com.unity3d.ads-mediation:unityads-adapter:5.11.0");
            probe.Files[AndroidSetup.MainTemplatePath] = FakeProbe.Template("com.unity3d.ads-mediation:mediation-sdk:9.5.0", "com.unity3d.ads-mediation:unityads-adapter:5.11.0");

            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Release, probe, _catalog);
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "native.sdk"));
            StringAssert.Contains("replaces native SDK 9.5.0 with https://s3.amazonaws.com/ssa.public/Dependencies-xmls/IronSource/9.7.0/IronSourceSDKDependencies.xml", plan.First(s => s.id == "native.sdk").how);
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "native.adapter"));
            StringAssert.Contains("replaces UnityAds adapter 5.11.0.0 with", plan.First(s => s.id == "native.adapter").how);
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "resolve"));
            var report = AdsValidator.Validate(_settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, _catalog);
            Assert.AreEqual(CheckResult.Fail, report.checks.First(c => c.id == "android.nativesdk").Result);
            Assert.AreEqual(CheckResult.Fail, report.checks.First(c => c.id == "android.adapter").Result);

            // the declarations replaced, the template still resolved to the old versions
            probe.Files[FakeProbe.NativeFile] = FakeProbe.DependencyXml("9.7.0", "com.unity3d.ads-mediation:mediation-sdk:9.7.0");
            probe.Files[FakeProbe.AdapterFile] = FakeProbe.DependencyXml("5.16.0.0", "com.unity3d.ads-mediation:unityads-adapter:5.15.0");
            plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Release, probe, _catalog);
            Assert.AreEqual(SetupStatus.Done, Status(plan, "native.sdk"));
            Assert.AreEqual(SetupStatus.Done, Status(plan, "native.adapter"));
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "resolve"), "the template names mediation-sdk 9.5.0");
            StringAssert.Contains("com.unity3d.ads-mediation:mediation-sdk:9.7.0", plan.First(s => s.id == "resolve").how);
            Assert.AreEqual(CheckResult.Fail, AdsValidator.Validate(_settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, _catalog).checks.First(c => c.id == "android.nativesdk").Result);

            probe.Files[AndroidSetup.MainTemplatePath] = FakeProbe.Template("com.unity3d.ads-mediation:mediation-sdk:9.7.0", "com.unity3d.ads-mediation:unityads-adapter:5.15.0");
            Assert.IsTrue(AndroidSetup.Plan(_settings, AdsValidator.Tab.Release, probe, _catalog).All(s => s.status == SetupStatus.Done));
            Assert.AreEqual(CheckResult.Pass, AdsValidator.Validate(_settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, _catalog).checks.First(c => c.id == "android.nativesdk").Result);
        }

        [Test]
        public void LevelPlaysProjectVersionsFile_Decides_WhenItListsTheInstalledPackage_AndThePackagesOwnFileOtherwise()
        {
            // issue #144: Gigawatt's refreshed project copy lists 9.7.0 for package 9.5.1, whose own file stops at 9.5.0
            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var sdk = _catalog.Provider(AdProviderKind.LevelPlay).sdk;
            var probe = new FakeProbe().Installed(AdProviderKind.LevelPlay);
            probe.Packages[FakeProbe.LevelPlayPackage] = "9.5.1";
            string packageOnly = VersionsJson.Replace("\"9.7.0\": { \"androidSdkVersion\": \"9.7.0\" }, \"9.6.0\": { \"androidSdkVersion\": \"9.6.0\" }, ", "");
            Assert.AreNotEqual(VersionsJson, packageOnly, "the fixture lost 9.7.0 and 9.6.0");
            probe.PackageFiles[FakeProbe.PackageVersionsFile] = packageOnly;
            Assert.IsTrue(AndroidSetup.TryNativeSdk(packageOnly, "9.5.1", out var native, out _));
            Assert.AreEqual("9.5.0", native, "the package's own file alone");

            probe.Files[FakeProbe.ProjectVersionsFile] = VersionsJson;
            Assert.AreEqual(VersionsJson, AndroidSetup.VersionsJsonFor(probe, sdk, out string source));
            Assert.AreEqual(FakeProbe.ProjectVersionsFile, source);
            StringAssert.Contains("IronSource/9.7.0/", AndroidSetup.Plan(_settings, AdsValidator.Tab.Test, probe, _catalog).First(s => s.id == "native.sdk").how);

            // a copy from before the package upgrade, which does not list the installed package
            probe.Packages[FakeProbe.LevelPlayPackage] = "9.6.0";
            string stale = VersionsJson.Replace("\"9.6.0\": { \"ironSourceSdkVersion\"", "\"9.0.0\": { \"ironSourceSdkVersion\"");
            Assert.AreNotEqual(VersionsJson, stale, "the stale copy still lists package 9.6.0");
            probe.Files[FakeProbe.ProjectVersionsFile] = stale;
            probe.PackageFiles[FakeProbe.PackageVersionsFile] = VersionsJson;
            Assert.AreEqual(VersionsJson, AndroidSetup.VersionsJsonFor(probe, sdk, out source));
            Assert.AreEqual(FakeProbe.LevelPlayPackage + "/Editor/Json/LevelPlayVersions.json", source);
        }

        [Test]
        public void LevelPlay_UnityAdsAdapter_IsPlanned_AtTheNewestVersionTheNativeSdkFits_AndTheValidatorFailsWithoutIt()
        {
            // issue #16: a build with the native SDK and no Unity Ads adapter answered every request with 509 No fill
            Assert.IsTrue(AndroidSetup.TryAdapter(VersionsJson, "9.4.0", "UnityAds", out var version, out var url));
            Assert.AreEqual("5.11.0.0", version, "package 9.4.0 allows native 9.4.5; 5.16.0.0 needs native 9.6.0 and 4.3.49.0 is for 8.x");
            Assert.AreEqual("https://s3.amazonaws.com/ssa.public/Dependencies-xmls/UnityAds/5.11.0.0/ISUnityAdsAdapterDependencies.xml", url, "the adapter's own URL, not the SDK's or another adapter's");
            Assert.IsFalse(AndroidSetup.TryAdapter(VersionsJson, "8.0.0", "UnityAds", out _, out _), "a package version the file does not list is not guessed");

            _settings.SelectedMediation = AdProviderKind.LevelPlay;
            var probe = new FakeProbe().Installed(AdProviderKind.LevelPlay).AndroidPrepared();
            probe.Packages["com.unity.services.levelplay"] = "9.5.1";
            probe.PackageFiles["com.unity.services.levelplay/Editor/Json/LevelPlayVersions.json"] = VersionsJson;
            probe.Files.Remove("Assets/LevelPlay/Editor/ISUnityAdsAdapterDependencies.xml");
            probe.Files[AndroidSetup.MainTemplatePath] = probe.Files[AndroidSetup.MainTemplatePath].Replace("unityads-adapter", "removed");

            var plan = AndroidSetup.Plan(_settings, AdsValidator.Tab.Release, probe, _catalog);
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "native.adapter"));
            Assert.AreEqual(SetupStatus.Pending, Status(plan, "resolve"), "the SDK alone in the template is not resolved");
            var report = AdsValidator.Validate(_settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, _catalog);
            Assert.AreEqual(CheckResult.Fail, report.checks.First(c => c.id == "android.adapter").Result);
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
