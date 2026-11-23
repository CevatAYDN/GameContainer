using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Editor.Ios
{
    /// <summary>One data type of the App Store privacy label, as the privacy manifest declares it.</summary>
    public sealed class PrivacyDataType
    {
        /// <summary>Full constant, for example <c>NSPrivacyCollectedDataTypeCrashData</c>.</summary>
        public string Type = string.Empty;
        public bool Linked;
        public bool Tracking;
        /// <summary>Full constants, for example <c>NSPrivacyCollectedDataTypePurposeAnalytics</c>.</summary>
        public List<string> Purposes = new List<string>();
    }

    /// <summary>One required-reason API category with its approved reason codes.</summary>
    public sealed class PrivacyAccessedApi
    {
        /// <summary>A category constant such as <c>NSPrivacyAccessedAPICategoryUserDefaults</c>; never a type name (ITMS-91054).</summary>
        public string Category = string.Empty;
        /// <summary>Reason codes such as <c>CA92.1</c>.</summary>
        public List<string> Reasons = new List<string>();
    }

    /// <summary>What a game declares in its <c>PrivacyInfo.xcprivacy</c>.</summary>
    public sealed class PrivacyManifestSpec
    {
        public bool Tracking;
        public List<string> TrackingDomains = new List<string>();
        public List<PrivacyDataType> DataTypes = new List<PrivacyDataType>();
        public List<PrivacyAccessedApi> AccessedApis = new List<PrivacyAccessedApi>();
    }

    /// <summary>
    /// Generates and validates the iOS privacy manifest (#436). The validation is the one Apple applies after upload and reports only by
    /// e-mail: ITMS-91054 hit a game whose <c>NSPrivacyAccessedAPIType</c> value was a type name instead of an
    /// <c>NSPrivacyAccessedAPICategory*</c> constant. Reason codes are checked for shape and presence, not against Apple's list, which
    /// this repository could not read when it was written (see the iOS port guide); the data-type and purpose constants are checked for
    /// their prefix only, for the same reason.
    /// </summary>
    public static class PrivacyManifest
    {
        public const string CategoryPrefix = "NSPrivacyAccessedAPICategory";
        public const string DataTypePrefix = "NSPrivacyCollectedDataType";
        public const string PurposePrefix = "NSPrivacyCollectedDataTypePurpose";

        /// <summary>The five required-reason categories Apple documents.</summary>
        public static readonly string[] Categories =
        {
            "NSPrivacyAccessedAPICategoryUserDefaults", "NSPrivacyAccessedAPICategoryFileTimestamp", "NSPrivacyAccessedAPICategorySystemBootTime",
            "NSPrivacyAccessedAPICategoryDiskSpace", "NSPrivacyAccessedAPICategoryActiveKeyboards"
        };

        private static readonly Regex ReasonShape = new Regex(@"^[0-9A-F]{4}\.[0-9]$", RegexOptions.CultureInvariant);

        /// <summary>The problems of a declaration; empty when it is valid. <paramref name="declaresTracking"/> is the game's statement on the App Store label.</summary>
        public static List<string> Validate(PrivacyManifestSpec spec, bool declaresTracking)
        {
            var errors = new List<string>();
            if (spec == null) { errors.Add("No privacy manifest declaration."); return errors; }

            if (!declaresTracking)
            {
                if (spec.Tracking) errors.Add("NSPrivacyTracking is true but the game declares no tracking (Apple rejects a tracking declaration the label contradicts).");
                if (spec.TrackingDomains.Count > 0) errors.Add("NSPrivacyTrackingDomains lists " + spec.TrackingDomains.Count + " domain(s) but the game declares no tracking.");
            }

            for (int i = 0; i < spec.DataTypes.Count; i++)
            {
                var type = spec.DataTypes[i];
                string where = "NSPrivacyCollectedDataTypes[" + i + "] ";
                if (!IsDataTypeConstant(type.Type)) errors.Add(where + "type '" + type.Type + "' must be an " + DataTypePrefix + "<Name> constant such as NSPrivacyCollectedDataTypeCrashData.");
                if (type.Purposes.Count == 0) errors.Add(where + "(" + type.Type + ") has no purpose.");
                foreach (string purpose in type.Purposes)
                {
                    if (purpose == null || !purpose.StartsWith(PurposePrefix, StringComparison.Ordinal) || purpose.Length == PurposePrefix.Length)
                        errors.Add(where + "(" + type.Type + ") purpose '" + purpose + "' must be an " + PurposePrefix + "<Name> constant such as NSPrivacyCollectedDataTypePurposeAnalytics.");
                }
                if (type.Tracking && !declaresTracking) errors.Add(where + "(" + type.Type + ") is marked tracking but the game declares no tracking.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.AccessedApis.Count; i++)
            {
                var api = spec.AccessedApis[i];
                string where = "NSPrivacyAccessedAPITypes[" + i + "] ";
                if (Array.IndexOf(Categories, api.Category) < 0)
                {
                    string hint = api.Category != null && api.Category.StartsWith("NSPrivacyAccessedAPIType", StringComparison.Ordinal) && !api.Category.StartsWith(CategoryPrefix, StringComparison.Ordinal)
                        ? " A type name was written where a category belongs (ITMS-91054)."
                        : string.Empty;
                    errors.Add(where + "NSPrivacyAccessedAPIType '" + api.Category + "' is not an NSPrivacyAccessedAPICategory constant (" + string.Join(", ", Categories) + ")." + hint);
                }
                else if (!seen.Add(api.Category))
                {
                    errors.Add(where + "category " + api.Category + " is declared twice; list its reasons in one entry.");
                }
                if (api.Reasons.Count == 0) errors.Add(where + "(" + api.Category + ") has no reason code.");
                foreach (string reason in api.Reasons)
                {
                    if (reason == null || !ReasonShape.IsMatch(reason)) errors.Add(where + "(" + api.Category + ") reason '" + reason + "' is not a reason code of the form CA92.1.");
                }
            }
            return errors;
        }

        private static bool IsDataTypeConstant(string value)
            => value != null && value.StartsWith(DataTypePrefix, StringComparison.Ordinal) && value.Length > DataTypePrefix.Length
               && !value.StartsWith(PurposePrefix, StringComparison.Ordinal);

        /// <summary>Reads a declaration back from the XML of an existing manifest (the ship check's input); an entry of the wrong shape is kept as written so <see cref="Validate"/> reports it.</summary>
        public static PrivacyManifestSpec Parse(string xml)
        {
            var root = PlistXml.ReadRoot(xml);
            var spec = new PrivacyManifestSpec { Tracking = root.TryGetValue("NSPrivacyTracking", out object tracking) && tracking is bool t && t };
            if (root.TryGetValue("NSPrivacyTrackingDomains", out object domains) && domains is List<object> domainList)
            {
                foreach (object domain in domainList) spec.TrackingDomains.Add(domain as string ?? string.Empty);
            }
            if (root.TryGetValue("NSPrivacyCollectedDataTypes", out object types) && types is List<object> typeList)
            {
                foreach (object entry in typeList)
                {
                    var dict = entry as Dictionary<string, object> ?? new Dictionary<string, object>();
                    var type = new PrivacyDataType
                    {
                        Type = Text(dict, "NSPrivacyCollectedDataType"),
                        Linked = dict.TryGetValue("NSPrivacyCollectedDataTypeLinked", out object linked) && linked is bool l && l,
                        Tracking = dict.TryGetValue("NSPrivacyCollectedDataTypeTracking", out object track) && track is bool k && k
                    };
                    type.Purposes.AddRange(Strings(dict, "NSPrivacyCollectedDataTypePurposes"));
                    spec.DataTypes.Add(type);
                }
            }
            if (root.TryGetValue("NSPrivacyAccessedAPITypes", out object apis) && apis is List<object> apiList)
            {
                foreach (object entry in apiList)
                {
                    var dict = entry as Dictionary<string, object> ?? new Dictionary<string, object>();
                    var api = new PrivacyAccessedApi { Category = Text(dict, "NSPrivacyAccessedAPIType") };
                    api.Reasons.AddRange(Strings(dict, "NSPrivacyAccessedAPITypeReasons"));
                    spec.AccessedApis.Add(api);
                }
            }
            return spec;
        }

        private static string Text(Dictionary<string, object> dict, string key) => dict.TryGetValue(key, out object value) ? value as string ?? string.Empty : string.Empty;

        private static IEnumerable<string> Strings(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object value) && value is List<object> list)
            {
                foreach (object item in list) yield return item as string ?? string.Empty;
            }
        }

        /// <summary>The manifest as XML property list text (LF, tab indent, the order of Apple's own examples).</summary>
        public static string ToXml(PrivacyManifestSpec spec)
        {
            var b = new StringBuilder();
            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            b.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            b.Append("<plist version=\"1.0\">\n<dict>\n");
            b.Append("\t<key>NSPrivacyTracking</key>\n\t").Append(spec.Tracking ? "<true/>" : "<false/>").Append('\n');
            AppendStrings(b, 1, "NSPrivacyTrackingDomains", spec.TrackingDomains);
            b.Append("\t<key>NSPrivacyCollectedDataTypes</key>\n");
            if (spec.DataTypes.Count == 0) b.Append("\t<array/>\n");
            else
            {
                b.Append("\t<array>\n");
                foreach (var type in spec.DataTypes)
                {
                    b.Append("\t\t<dict>\n");
                    b.Append("\t\t\t<key>NSPrivacyCollectedDataType</key>\n\t\t\t<string>").Append(PlistXml.Escape(type.Type)).Append("</string>\n");
                    b.Append("\t\t\t<key>NSPrivacyCollectedDataTypeLinked</key>\n\t\t\t").Append(type.Linked ? "<true/>" : "<false/>").Append('\n');
                    b.Append("\t\t\t<key>NSPrivacyCollectedDataTypeTracking</key>\n\t\t\t").Append(type.Tracking ? "<true/>" : "<false/>").Append('\n');
                    AppendStrings(b, 3, "NSPrivacyCollectedDataTypePurposes", type.Purposes);
                    b.Append("\t\t</dict>\n");
                }
                b.Append("\t</array>\n");
            }
            b.Append("\t<key>NSPrivacyAccessedAPITypes</key>\n");
            if (spec.AccessedApis.Count == 0) b.Append("\t<array/>\n");
            else
            {
                b.Append("\t<array>\n");
                foreach (var api in spec.AccessedApis)
                {
                    b.Append("\t\t<dict>\n");
                    b.Append("\t\t\t<key>NSPrivacyAccessedAPIType</key>\n\t\t\t<string>").Append(PlistXml.Escape(api.Category)).Append("</string>\n");
                    AppendStrings(b, 3, "NSPrivacyAccessedAPITypeReasons", api.Reasons);
                    b.Append("\t\t</dict>\n");
                }
                b.Append("\t</array>\n");
            }
            b.Append("</dict>\n</plist>\n");
            return b.ToString();
        }

        private static void AppendStrings(StringBuilder b, int depth, string key, List<string> values)
        {
            string tabs = new string('\t', depth);
            b.Append(tabs).Append("<key>").Append(key).Append("</key>\n");
            if (values.Count == 0) { b.Append(tabs).Append("<array/>\n"); return; }
            b.Append(tabs).Append("<array>\n");
            foreach (string value in values) b.Append(tabs).Append("\t<string>").Append(PlistXml.Escape(value)).Append("</string>\n");
            b.Append(tabs).Append("</array>\n");
        }
    }
}
