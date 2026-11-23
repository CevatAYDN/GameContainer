using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Editor.Ios
{
    /// <summary>What the game declares for its iOS <c>Info.plist</c>.</summary>
    public sealed class InfoPlistOptions
    {
        /// <summary>Written as <c>CFBundleDisplayName</c> when not empty.</summary>
        public string DisplayName = string.Empty;
        /// <summary>Written as <c>CFBundleDevelopmentRegion</c> when the plist has none.</summary>
        public string DevelopmentRegion = "en";
        /// <summary>Written as <c>CFBundleLocalizations</c> (replacing the plist's) when not empty.</summary>
        public List<string> Languages = new List<string>();
        /// <summary>The game's statement on the App Store label. False removes <c>NSUserTrackingUsageDescription</c>.</summary>
        public bool DeclaresTracking;
    }

    public sealed class InfoPlistResult
    {
        public string Xml = string.Empty;
        public readonly List<string> Changes = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>The Info.plist decisions of the iOS App Store kit (#436), as text functions the post-process applies to the exported file.</summary>
    public static class InfoPlistRules
    {
        public const string TrackingKey = "NSUserTrackingUsageDescription";
        public const string EncryptionKey = "ITSAppUsesNonExemptEncryption";

        public static InfoPlistResult Apply(string xml, InfoPlistOptions options)
        {
            var result = new InfoPlistResult();
            if (PlistXml.AddBoolIfAbsent(ref xml, EncryptionKey, false)) result.Changes.Add(EncryptionKey + " = false");
            if (!string.IsNullOrEmpty(options.DevelopmentRegion) && PlistXml.AddStringIfAbsent(ref xml, "CFBundleDevelopmentRegion", options.DevelopmentRegion))
                result.Changes.Add("CFBundleDevelopmentRegion = " + options.DevelopmentRegion);
            if (!string.IsNullOrEmpty(options.DisplayName) && PlistXml.SetString(ref xml, "CFBundleDisplayName", options.DisplayName))
                result.Changes.Add("CFBundleDisplayName = " + options.DisplayName);
            if (options.Languages.Count > 0 && PlistXml.SetStringArray(ref xml, "CFBundleLocalizations", options.Languages))
                result.Changes.Add("CFBundleLocalizations = " + string.Join(", ", options.Languages));

            if (options.DeclaresTracking)
            {
                if (!PlistXml.HasKey(xml, TrackingKey))
                    result.Errors.Add("The game declares tracking but Info.plist has no " + TrackingKey + "; the app would crash or be rejected when it asks. Set it in Player Settings (iOS) or turn tracking off.");
            }
            else if (PlistXml.RemoveKey(ref xml, TrackingKey))
            {
                result.Changes.Add(TrackingKey + " removed (the game declares no tracking)");
            }

            result.Xml = xml;
            return result;
        }

        /// <summary>The text of a <c>InfoPlist.strings</c> file with one entry; quotes, backslashes and line breaks of the value are escaped.</summary>
        public static string InfoPlistStrings(string key, string value)
        {
            var b = new StringBuilder("\"").Append(Escape(key)).Append("\" = \"").Append(Escape(value)).Append("\";\n");
            return b.ToString();
        }

        private static string Escape(string text)
            => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
    }
}
