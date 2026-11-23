using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// The iOS App Store kit's settings (#436). A game turns the kit on by creating one asset (Assets > Create > Nexus > iOS App Store Kit);
    /// without an asset the post-process does nothing. The privacy manifest lists the same data types the App Store privacy label does.
    /// </summary>
    [CreateAssetMenu(fileName = "IosKitSettings", menuName = "Nexus/iOS App Store Kit")]
    public sealed class IosKitSettings : ScriptableObject
    {
        [Serializable]
        public sealed class LanguageEntry
        {
            [Tooltip("BCP 47 / lproj code, for example en or tr.")] public string code = "en";
            [Tooltip("The app name the home screen shows in this language.")] public string displayName = string.Empty;
        }

        [Serializable]
        public sealed class DataTypeEntry
        {
            [Tooltip("Full constant, for example NSPrivacyCollectedDataTypeCrashData.")] public string type = string.Empty;
            public bool linked = true;
            public bool tracking;
            [Tooltip("Full constants, for example NSPrivacyCollectedDataTypePurposeAnalytics.")] public List<string> purposes = new List<string>();
        }

        [Serializable]
        public sealed class AccessedApiEntry
        {
            [Tooltip("A category constant, for example NSPrivacyAccessedAPICategoryUserDefaults (never a type name: ITMS-91054).")] public string category = string.Empty;
            [Tooltip("Reason codes, for example CA92.1.")] public List<string> reasons = new List<string>();
        }

        [Tooltip("The game's statement on the App Store label. False: no NSUserTrackingUsageDescription, no tracking in the manifest, a ship check that refuses a tracking text.")]
        public bool declaresTracking;
        [Tooltip("Minimum iOS the pods are built for (Podfile post_install). Empty leaves the Podfile alone.")]
        public string podsMinimumIos = "15.0";
        public string developmentRegion = "en";
        public List<LanguageEntry> languages = new List<LanguageEntry> { new LanguageEntry() };
        public List<string> trackingDomains = new List<string>();
        public List<DataTypeEntry> dataTypes = new List<DataTypeEntry>();
        public List<AccessedApiEntry> accessedApis = new List<AccessedApiEntry>();

        /// <summary>The first settings asset of the project, or null when the game has not turned the kit on.</summary>
        public static IosKitSettings Find()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:IosKitSettings"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<IosKitSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) return asset;
            }
            return null;
        }

        public InfoPlistOptions ToInfoPlistOptions()
        {
            var options = new InfoPlistOptions { DisplayName = PlayerSettings.productName, DevelopmentRegion = developmentRegion, DeclaresTracking = declaresTracking };
            foreach (var language in languages) options.Languages.Add(language.code);
            return options;
        }

        public PrivacyManifestSpec ToManifestSpec()
        {
            var spec = new PrivacyManifestSpec { Tracking = declaresTracking };
            spec.TrackingDomains.AddRange(trackingDomains);
            foreach (var type in dataTypes)
            {
                var entry = new PrivacyDataType { Type = type.type, Linked = type.linked, Tracking = type.tracking };
                entry.Purposes.AddRange(type.purposes);
                spec.DataTypes.Add(entry);
            }
            foreach (var api in accessedApis)
            {
                var entry = new PrivacyAccessedApi { Category = api.category };
                entry.Reasons.AddRange(api.reasons);
                spec.AccessedApis.Add(entry);
            }
            return spec;
        }
    }
}
