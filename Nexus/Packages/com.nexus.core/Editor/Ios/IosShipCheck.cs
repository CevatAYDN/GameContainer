using System;
using System.Collections.Generic;

namespace Nexus.Editor.Ios
{
    /// <summary>Everything the ship checks read from an archive; the Unity-side runner fills it from the <c>.xcarchive</c>.</summary>
    public sealed class IosShipInput
    {
        /// <summary>The archived app's <c>Info.plist</c> as XML.</summary>
        public string InfoPlistXml = string.Empty;
        /// <summary>The bundle's <c>PrivacyInfo.xcprivacy</c> as XML, or null when the bundle has none.</summary>
        public string PrivacyManifestXml;
        /// <summary>UnityFramework's <c>OTHER_LDFLAGS</c> (Release), or null when it could not be read.</summary>
        public string UnityFrameworkLdFlags;
        public bool DeclaresTracking;
        /// <summary>Keys that must be boolean false, for example Firebase's collection flags.</summary>
        public List<string> MustBeFalse = new List<string>();
        /// <summary>Highest build number already uploaded to App Store Connect; null skips the check.</summary>
        public long? LastUploadedBuild;
        /// <summary>Class names that must be in the UnityFramework binary (LevelPlay's <c>IS*Adapter</c>s); empty skips.</summary>
        public List<string> RequiredBinaryClasses = new List<string>();
        /// <summary>True when the binary holds the text; the runner reads the file once.</summary>
        public Func<string, bool> BinaryContains = _ => false;
    }

    public sealed class IosShipCheckResult
    {
        public string Name = string.Empty;
        public bool Passed;
        public string Detail = string.Empty;
    }

    /// <summary>
    /// The ship checks of the iOS App Store kit (#436): what Wickling's first submission shipped wrong, read from the archive and not
    /// from the settings that should have produced it. A failed check refuses the export.
    /// </summary>
    public static class IosShipCheck
    {
        public static List<IosShipCheckResult> Evaluate(IosShipInput input)
        {
            var results = new List<IosShipCheckResult>();
            Dictionary<string, object> info = null;
            try { info = PlistXml.ReadRoot(input.InfoPlistXml); }
            catch (FormatException ex) { Add(results, "Info.plist is readable", false, ex.Message); }

            if (info != null)
            {
                Add(results, ITS, info.TryGetValue(InfoPlistRules.EncryptionKey, out object enc) && enc is bool e && !e, "must be false, else App Store Connect asks the export compliance question");
                bool hasTracking = info.ContainsKey(InfoPlistRules.TrackingKey);
                Add(results, "NSUserTrackingUsageDescription matches the tracking declaration",
                    hasTracking == input.DeclaresTracking,
                    input.DeclaresTracking ? "tracking is declared, the text is missing" : "no tracking is declared, the text must not be there");
                foreach (string key in input.MustBeFalse)
                {
                    Add(results, key + " is false (nothing sent before the consent answer)", info.TryGetValue(key, out object v) && v is bool b && !b, "missing or not boolean false");
                }
                info.TryGetValue("CFBundleVersion", out object build);
                var buildText = build as string ?? string.Empty;
                if (input.LastUploadedBuild.HasValue)
                {
                    string detail = BuildNumberProblem(buildText, input.LastUploadedBuild.Value);
                    Add(results, "CFBundleVersion is above the last uploaded build", detail == null, detail ?? ("build " + buildText + " > " + input.LastUploadedBuild.Value));
                }
            }

            if (input.PrivacyManifestXml == null)
            {
                Add(results, "the app bundle carries PrivacyInfo.xcprivacy", false, "missing from the bundle");
            }
            else
            {
                try
                {
                    var errors = PrivacyManifest.Validate(PrivacyManifest.Parse(input.PrivacyManifestXml), input.DeclaresTracking);
                    Add(results, "PrivacyInfo.xcprivacy is valid (category names, reasons; ITMS-91054)", errors.Count == 0, errors.Count == 0 ? "valid" : string.Join(" | ", errors));
                }
                catch (FormatException ex)
                {
                    Add(results, "PrivacyInfo.xcprivacy is readable", false, ex.Message);
                }
            }

            bool objc = input.UnityFrameworkLdFlags != null && HasObjC(input.UnityFrameworkLdFlags);
            Add(results, "UnityFramework links with -ObjC", objc, input.UnityFrameworkLdFlags == null ? "OTHER_LDFLAGS could not be read" : "OTHER_LDFLAGS lacks -ObjC");
            foreach (string cls in input.RequiredBinaryClasses)
            {
                Add(results, cls + " is in the UnityFramework binary", input.BinaryContains(cls), "not found; the mediation would answer 509 No fill");
            }
            return results;
        }

        private const string ITS = "ITSAppUsesNonExemptEncryption is false";

        /// <summary>Null when <paramref name="build"/> is a number above <paramref name="lastUploaded"/>; else the reason.</summary>
        public static string BuildNumberProblem(string build, long lastUploaded)
        {
            if (!long.TryParse(build, out long number)) return "CFBundleVersion '" + build + "' is not a plain number";
            return number > lastUploaded ? null : "build " + number + " is not above the last upload " + lastUploaded + "; a number once uploaded is spent";
        }

        private static bool HasObjC(string flags)
        {
            foreach (string entry in flags.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (entry.Trim('"', '\'') == "-ObjC") return true;
            }
            return false;
        }

        private static void Add(List<IosShipCheckResult> results, string name, bool passed, string detail)
            => results.Add(new IosShipCheckResult { Name = name, Passed = passed, Detail = passed ? "ok" : detail });
    }
}
