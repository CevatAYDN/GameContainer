using System;
using System.Collections.Generic;
using Nexus.Editor.Ios;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    /// <summary>The iOS App Store kit's pure rules (#436): privacy manifest, Info.plist, Podfile and ship checks. The harness runs these too.</summary>
    public sealed class FullReviewIosKitTests
    {
        private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n";

        private static string Plist(params string[] entries) => Header + string.Join(string.Empty, entries) + "</dict>\n</plist>\n";

        private const string Name = "\t<key>CFBundleName</key>\n\t<string>Game</string>\n";
        private const string Tracking = "\t<key>NSUserTrackingUsageDescription</key>\n\t<string>Track \"me\" &amp; more</string>\n";
        private const string Capabilities = "\t<key>UIRequiredDeviceCapabilities</key>\n\t<array>\n\t\t<string>arm64</string>\n\t</array>\n";

        private static PrivacyManifestSpec Valid()
        {
            var spec = new PrivacyManifestSpec();
            spec.DataTypes.Add(new PrivacyDataType { Type = "NSPrivacyCollectedDataTypeCrashData", Linked = true, Purposes = { "NSPrivacyCollectedDataTypePurposeAnalytics" } });
            spec.AccessedApis.Add(new PrivacyAccessedApi { Category = "NSPrivacyAccessedAPICategoryUserDefaults", Reasons = { "CA92.1" } });
            spec.AccessedApis.Add(new PrivacyAccessedApi { Category = "NSPrivacyAccessedAPICategoryFileTimestamp", Reasons = { "C617.1", "0A2A.1" } });
            return spec;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_ATypeNameWhereACategoryBelongs_IsRejectedAsItms91054()
        {
            var spec = Valid();
            spec.AccessedApis[0].Category = "NSPrivacyAccessedAPITypeUserDefaults";

            var errors = PrivacyManifest.Validate(spec, false);

            Require(errors.Count == 1 && errors[0].Contains("ITMS-91054") && errors[0].Contains("NSPrivacyAccessedAPICategory"), "A type name in NSPrivacyAccessedAPIType must fail with ITMS-91054: " + string.Join(" | ", errors));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_AValidManifest_PassesAndSurvivesGenerateThenRead()
        {
            var spec = Valid();
            Require(PrivacyManifest.Validate(spec, false).Count == 0, "A declaration of categories with reason codes is valid.");

            string xml = PrivacyManifest.ToXml(spec);
            var back = PrivacyManifest.Parse(xml);

            Require(PrivacyManifest.Validate(back, false).Count == 0, "The generated file must validate when read back.");
            Require(back.AccessedApis.Count == 2 && back.AccessedApis[1].Reasons.Count == 2 && back.DataTypes[0].Type == "NSPrivacyCollectedDataTypeCrashData" && back.DataTypes[0].Linked && !back.Tracking,
                "Types, reasons and flags must survive the round trip.");
            Require(xml.Contains("<key>NSPrivacyAccessedAPIType</key>\n\t\t\t<string>NSPrivacyAccessedAPICategoryUserDefaults</string>"), "The category goes under NSPrivacyAccessedAPIType.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_TrackingInTheManifestWithoutADeclaration_IsRejected()
        {
            var spec = Valid();
            spec.Tracking = true;
            spec.TrackingDomains.Add("track.example.com");
            spec.DataTypes[0].Tracking = true;

            Require(PrivacyManifest.Validate(spec, false).Count == 3, "NSPrivacyTracking, the domain list and the tracking data type each fail when the game declares no tracking.");
            Require(PrivacyManifest.Validate(spec, true).Count == 0, "The same manifest is accepted when the game declares tracking.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_MalformedDeclarations_AreReported()
        {
            var spec = new PrivacyManifestSpec();
            spec.DataTypes.Add(new PrivacyDataType { Type = "CrashData" });
            spec.DataTypes.Add(new PrivacyDataType { Type = "NSPrivacyCollectedDataTypeCrashData", Purposes = { "Analytics" } });
            spec.AccessedApis.Add(new PrivacyAccessedApi { Category = "NSPrivacyAccessedAPICategoryDiskSpace" });
            spec.AccessedApis.Add(new PrivacyAccessedApi { Category = "NSPrivacyAccessedAPICategoryDiskSpace", Reasons = { "e174.1" } });

            var errors = PrivacyManifest.Validate(spec, false);

            Require(errors.Exists(e => e.Contains("'CrashData' must be")), "A data type without the constant prefix fails.");
            Require(errors.Exists(e => e.Contains("has no purpose")), "A data type without purposes fails.");
            Require(errors.Exists(e => e.Contains("purpose 'Analytics'")), "A purpose without the constant prefix fails.");
            Require(errors.Exists(e => e.Contains("has no reason code")), "A category without reasons fails.");
            Require(errors.Exists(e => e.Contains("declared twice")), "A category declared twice fails.");
            Require(errors.Exists(e => e.Contains("'e174.1'")), "A reason that is not of the form CA92.1 fails.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_InfoPlist_KeepsWhatTheGameSet_AndRemovesATrackingTextTheLabelContradicts()
        {
            string xml = Plist(Name, Tracking, "\t<key>ITSAppUsesNonExemptEncryption</key>\n\t<true/>\n", Capabilities);
            var options = new InfoPlistOptions { DisplayName = "Wickling", Languages = { "en", "tr" } };

            var result = InfoPlistRules.Apply(xml, options);

            var root = PlistXml.ReadRoot(result.Xml);
            Require(result.Errors.Count == 0, "No error expected: " + string.Join(" ", result.Errors));
            Require(root["ITSAppUsesNonExemptEncryption"] is bool b && b, "A value the game set stays as it is.");
            Require(!root.ContainsKey("NSUserTrackingUsageDescription"), "The tracking text goes when the game declares no tracking.");
            Require(root["CFBundleDisplayName"] as string == "Wickling" && root["CFBundleDevelopmentRegion"] as string == "en", "Display name and region are written.");
            Require(root["CFBundleLocalizations"] is List<object> l && l.Count == 2 && (string)l[1] == "tr", "Localizations are written.");
            Require(root["UIRequiredDeviceCapabilities"] is List<object> c && c.Count == 1, "Entries the kit does not own are untouched.");
            Require(result.Xml.StartsWith(Header, StringComparison.Ordinal) && result.Xml.Contains("<!DOCTYPE plist PUBLIC") && !result.Xml.Contains("[]>"), "The XML declaration and DOCTYPE stay as Unity wrote them.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_InfoPlist_SetsEncryptionFalseWhenAbsent_AndIsStableOnASecondRun()
        {
            string first = InfoPlistRules.Apply(Plist(Name), new InfoPlistOptions { Languages = { "en" } }).Xml;
            var second = InfoPlistRules.Apply(first, new InfoPlistOptions { Languages = { "en" } });

            Require(PlistXml.ReadRoot(first)["ITSAppUsesNonExemptEncryption"] is bool b && !b, "Export compliance key is false when the game set none.");
            Require(second.Xml == first && second.Changes.Count == 0, "A second run changes nothing.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_InfoPlist_DeclaredTrackingNeedsItsUsageText()
        {
            var options = new InfoPlistOptions { DeclaresTracking = true };

            Require(InfoPlistRules.Apply(Plist(Name), options).Errors.Count == 1, "Declared tracking without NSUserTrackingUsageDescription is an error.");
            var kept = InfoPlistRules.Apply(Plist(Name, Tracking), options);
            Require(kept.Errors.Count == 0 && PlistXml.HasKey(kept.Xml, "NSUserTrackingUsageDescription"), "Declared tracking keeps the text.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_InfoPlistStrings_EscapesQuotesBackslashesAndLineBreaks()
        {
            Require(InfoPlistRules.InfoPlistStrings("CFBundleDisplayName", "A \"B\" \\ C\nD") == "\"CFBundleDisplayName\" = \"A \\\"B\\\" \\\\ C\\nD\";\n", "The value must be escaped for a .strings file.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_Podfile_GetsTheMinimumOnce_AlsoInsideAnExistingPostInstall()
        {
            string plain = PodfilePatch.EnsureMinimumIos("platform :ios, '15.0'\n", "15.0", out bool changed);
            Require(changed && plain.Contains("post_install do |installer|") && plain.Contains("= '15.0'") && plain.TrimEnd().EndsWith("end", StringComparison.Ordinal), "A Podfile without post_install gets one.");
            PodfilePatch.EnsureMinimumIos(plain, "15.0", out bool again);
            Require(!again, "The marker makes a second call a no-op.");

            string existing = PodfilePatch.EnsureMinimumIos("target 'x' do\nend\npost_install do |installer|\n  puts 'hi'\nend\n", "16.0", out bool merged);
            Require(merged && System.Text.RegularExpressions.Regex.Matches(existing, "post_install do").Count == 1 && existing.IndexOf("IPHONEOS_DEPLOYMENT_TARGET", StringComparison.Ordinal) < existing.IndexOf("puts 'hi'", StringComparison.Ordinal),
                "An existing post_install block gets the loop inside it, not a second block.");

            bool threw = false;
            try { PodfilePatch.EnsureMinimumIos("", "fifteen", out _); } catch (ArgumentException) { threw = true; }
            Require(threw, "A minimum that is not a version is refused.");
        }

        private static IosShipInput GoodInput()
        {
            var input = new IosShipInput
            {
                InfoPlistXml = Plist(Name, "\t<key>CFBundleVersion</key>\n\t<string>10009</string>\n", "\t<key>ITSAppUsesNonExemptEncryption</key>\n\t<false/>\n", "\t<key>FIREBASE_ANALYTICS_COLLECTION_ENABLED</key>\n\t<false/>\n"),
                PrivacyManifestXml = PrivacyManifest.ToXml(Valid()),
                UnityFrameworkLdFlags = "$(inherited) -ObjC -lc++",
                LastUploadedBuild = 10008,
                BinaryContains = text => text == "ISUnityAdsAdapter"
            };
            input.MustBeFalse.Add("FIREBASE_ANALYTICS_COLLECTION_ENABLED");
            input.RequiredBinaryClasses.Add("ISUnityAdsAdapter");
            return input;
        }

        private static string Failures(IosShipInput input)
        {
            var failed = new List<string>();
            foreach (var check in IosShipCheck.Evaluate(input)) if (!check.Passed) failed.Add(check.Name);
            return string.Join(" | ", failed);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void IosKit_ShipCheck_PassesAGoodArchive_AndNamesEachDefectOfTheFirstSubmission()
        {
            Require(Failures(GoodInput()) == string.Empty, "A good archive passes: " + Failures(GoodInput()));

            var input = GoodInput();
            input.PrivacyManifestXml = PrivacyManifest.ToXml(Valid()).Replace("NSPrivacyAccessedAPICategoryUserDefaults", "NSPrivacyAccessedAPITypeUserDefaults");
            Require(Failures(input).Contains("PrivacyInfo.xcprivacy is valid"), "ITMS-91054 in the bundle's manifest fails.");

            input = GoodInput();
            input.PrivacyManifestXml = null;
            Require(Failures(input).Contains("carries PrivacyInfo.xcprivacy"), "A bundle without the manifest fails.");

            input = GoodInput();
            input.UnityFrameworkLdFlags = "$(inherited)";
            Require(Failures(input).Contains("-ObjC"), "UnityFramework without -ObjC fails.");

            input = GoodInput();
            input.BinaryContains = _ => false;
            Require(Failures(input).Contains("ISUnityAdsAdapter"), "An adapter class missing from the binary fails.");

            input = GoodInput();
            input.InfoPlistXml = Plist(Name, Tracking, "\t<key>CFBundleVersion</key>\n\t<string>10009</string>\n");
            string text = Failures(input);
            Require(text.Contains("ITSAppUsesNonExemptEncryption") && text.Contains("NSUserTrackingUsageDescription") && text.Contains("FIREBASE_ANALYTICS_COLLECTION_ENABLED"),
                "A missing encryption key, a tracking text with no tracking declared and a Firebase flag that is not false each fail: " + text);

            input = GoodInput();
            input.LastUploadedBuild = 10009;
            Require(Failures(input).Contains("above the last uploaded build"), "A build number already uploaded fails.");
            Require(IosShipCheck.BuildNumberProblem("abc", 1) != null && IosShipCheck.BuildNumberProblem("2", 1) == null, "Only a plain number above the last upload passes.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
