#if UNITY_EDITOR
using System;
using System.Linq;
using Nexus.Ads;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using NUnit.Framework;
using UnityEngine;
using ProjectProbe = Nexus.Ads.Tests.FakeProbe;

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsValidationTests
    {
        [Test]
        public void A08_CommentedPermissionRemovalCannotPassChildDirectedValidation()
        {
            var settings = Settings(AdProviderKind.None);
            try
            {
                settings.AdFreeRelease = true;
                settings.ChildDirected = true;
                var probe = new ProjectProbe();
                const string declaration = "<uses-permission android:name=\"com.google.android.gms.permission.AD_ID\" tools:node=\"remove\"/>";
                const string open = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\" xmlns:tools=\"http://schemas.android.com/tools\">";
                var catalog = PlatformCatalog.Load();

                probe.AndroidManifest = open + declaration + "</manifest>";
                Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "android.adid").Result,
                    Is.EqualTo(CheckResult.Pass), "An effective permission-removal element is the positive control.");
                probe.AndroidManifest = open + "<!-- " + declaration + " -->" + "</manifest>";

                Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "android.adid").Result,
                    Is.EqualTo(CheckResult.Fail), "An XML comment cannot remove the permission from the merged manifest.");
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void A08_PermissionRemovalRequiresBoundNamespacesAndAnUnscopedManifestChild()
        {
            var settings = Settings(AdProviderKind.None);
            try
            {
                settings.AdFreeRelease = true;
                settings.ChildDirected = true;
                var probe = new ProjectProbe();
                var catalog = PlatformCatalog.Load();
                const string declaration = "<uses-permission android:name=\"com.google.android.gms.permission.AD_ID\" tools:node=\"remove\"/>";
                const string open = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\" xmlns:tools=\"http://schemas.android.com/tools\">";

                probe.AndroidManifest = "<manifest xmlns:a='http://schemas.android.com/apk/res/android' xmlns:t='http://schemas.android.com/tools'>"
                    + "<uses-permission t:node='remove' a:name='com.google.android.gms.permission.AD_ID'/></manifest>";
                Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "android.adid").Result,
                    Is.EqualTo(CheckResult.Pass), "Namespace URIs, not attribute prefixes or quote style, identify removal.");

                foreach (string manifest in new[]
                {
                    "<manifest>" + declaration + "</manifest>",
                    "<manifest xmlns:android='urn:not-android' xmlns:tools='http://schemas.android.com/tools'>" + declaration + "</manifest>",
                    open + "<application>" + declaration + "</application></manifest>",
                    open + "<![CDATA[" + declaration + "]]></manifest>",
                    open + "<uses-permission android:name=\"com.google.android.gms.permission.AD_ID\" tools:node=\"remove\" tools:selector=\"example.library\"/></manifest>",
                    open + declaration + "<uses-permission android:name=\"com.google.android.gms.permission.AD_ID\"/></manifest>",
                    open + declaration,
                    "<!DOCTYPE manifest [<!ENTITY ignored 'value'>]>" + open + declaration + "</manifest>",
                })
                {
                    probe.AndroidManifest = manifest;
                    Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "android.adid").Result,
                        Is.EqualTo(CheckResult.Fail), "Only an effective, unambiguous removal may pass: " + manifest);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void A09_MissingPropertiesTemplateFailsValidationWhenTheSetupPlanRequiresIt()
        {
            var settings = Settings(AdProviderKind.LevelPlay);
            try
            {
                var probe = new ProjectProbe().Installed(AdProviderKind.LevelPlay).AndroidPrepared("9.6.0");
                var catalog = PlatformCatalog.Load();
                Assert.That(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog).Passed,
                    Is.True, "The complete prepared project is the positive control.");

                Assert.That(probe.Files.Remove(AndroidSetup.PropertiesTemplatePath), Is.True,
                    "Only the properties template is removed from the prepared fixture.");
                Assert.That(AndroidSetup.Plan(settings, AdsValidator.Tab.Release, probe, catalog)
                    .Single(step => step.id == "gradle.properties").status, Is.EqualTo(SetupStatus.Pending));

                Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "android.gradle").Result,
                    Is.EqualTo(CheckResult.Fail), "A required pending Gradle step must not be reported as a passing build check.");
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void A12_DistinctRawNamesCannotGenerateTheSameConstant()
        {
            var placements = new[]
            {
                new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded },
                new PlacementEntry { Name = "ShopRefill", Format = AdFormat.Rewarded },
            };
            Assert.Throws<InvalidOperationException>(() => AdPlacementsGenerator.Render(placements),
                "Both names become ShopRefill; reject the collision before writing uncompilable source.");
        }

        [Test]
        public void A12_NamesThatBecomeEmptyOrDigitLeadingConstantsAreRejected()
        {
            foreach (string name in new[] { "_", "_1" })
                Assert.Throws<InvalidOperationException>(
                    () => AdPlacementsGenerator.Render(new[] { new PlacementEntry { Name = name, Format = AdFormat.Rewarded } }),
                    "The emitted identifier for '" + name + "' is invalid even though the raw placement name passes its regex.");
        }

        [Test]
        public void A12_ValidatorRejectsTheCollisionTheGeneratorWouldEmit()
        {
            var settings = Settings(AdProviderKind.None);
            try
            {
                settings.AdFreeRelease = true;
                settings.Placements.Clear();
                settings.Placements.Add(new PlacementEntry { Name = "shop_refill", Format = AdFormat.Rewarded });
                settings.Placements.Add(new PlacementEntry { Name = "ShopRefill", Format = AdFormat.Rewarded });
                var report = AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android,
                    new ProjectProbe(), PlatformCatalog.Load());

                Assert.That(Check(report, "placements.names").Result, Is.EqualTo(CheckResult.Fail),
                    "Validation must not accept placement names that generate duplicate declarations.");
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void A12_GeneratorAndValidatorRejectInvalidOrContainerConflictingConstants()
        {
            var settings = Settings(AdProviderKind.None);
            try
            {
                settings.AdFreeRelease = true;
                var probe = new ProjectProbe();
                var catalog = PlatformCatalog.Load();
                foreach (string name in new[] { "_", "_1", "AdPlacements", "ad_placements", "reward\n" })
                {
                    settings.Placements.Clear();
                    settings.Placements.Add(new PlacementEntry { Name = name, Format = AdFormat.Rewarded });

                    Assert.Throws<InvalidOperationException>(() => AdPlacementsGenerator.Render(settings.Placements),
                        "A placement must not emit an invalid declaration or a member named after its containing class.");
                    Assert.That(Check(AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog), "placements.names").Result,
                        Is.EqualTo(CheckResult.Fail), "Validation and generation must apply the same naming rules.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        private static AdsCheck Check(AdsValidationReport report, string id) => report.checks.Single(check => check.id == id);

        private static AdsSettings Settings(AdProviderKind provider)
        {
            var settings = ScriptableObject.CreateInstance<AdsSettings>();
            settings.SelectedMediation = provider;
            settings.Consent = ConsentSource.GameFlow;
            settings.PrivacyPolicyUrl = "https://example.invalid/privacy";
            settings.Placements.Add(new PlacementEntry { Name = "reward", Format = AdFormat.Rewarded });
            settings.Release.LevelPlay.Android.AppKey = "fixture-key";
            settings.Release.LevelPlay.Android.RewardedUnitId = "fixture-reward";
            settings.Test.LevelPlay.Android.AppKey = "fixture-key";
            settings.Test.LevelPlay.Android.RewardedUnitId = "fixture-reward";
            return settings;
        }
    }
}
#endif
