using System;
using System.IO;
using UnityEditor.PackageManager;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    // JsonUtility mirror of Editor/PlatformCatalog.json. The file is the data; nothing in the editor
    // hardcodes a link, a sample id or a step. Fields match the JSON keys exactly.

    [Serializable] public sealed class CatalogFamilies { public bool listed; public string row; public string source; }
    [Serializable] public sealed class CatalogUpm { public string package; public string registry; public string[] scopes; public string installedBy; public string versionSeen; public string versionSource; public string githubLatestRelease; }
    [Serializable] public sealed class CatalogSdk { public CatalogUpm upm; public string unitypackage; public string define; public string rootNamespace; public string assembly; public string init; public int minAndroidApi; public string minIos; public string sdkKeyLocation; public bool jetifier; public string minUnity; public string settingsAsset; public string testDevicesApi; public string adIdPermission;
        // Android setup (AndroidSetup): the Nexus provider package, where the dependency resolver comes from, the native SDK declaration and the Gradle lines
        public string providerPackage; public string resolver; public string resolverNote; public string nativeDependencyFile; public string nativeVersionsJson; public string nativeSource; public string resolvedMarker; public string[] gradleLines; public string gradleNote; public string gradlePropertiesNote;
        public CatalogAdapter nativeAdapter; }
    /// <summary>A network adapter the mediation needs to fill at all (LevelPlay: Unity Ads), declared like the native SDK from the package's versions file.</summary>
    [Serializable] public sealed class CatalogAdapter { public string key; public string dependencyFile; public string resolvedMarker; public string note; }
    [Serializable] public sealed class CatalogAdMobIds { public string appId; public string appOpen; public string banner; public string adaptiveBanner; public string interstitial; public string rewarded; public string rewardedInterstitial; public string native; }
    [Serializable] public sealed class CatalogAppIdSource { public string android; public string ios; }
    [Serializable] public sealed class CatalogTestDevices { public string where; public string source; public string api; public string dashboard; public string logLine; }
    [Serializable] public sealed class CatalogTestSuite { public string metadataKey; public string metadataValue; public string launch; public string minSdk; public string orientation; public string source; }
    [Serializable] public sealed class CatalogTestData { public bool published; public string note; public string editorMockKey; public string source; public CatalogAppIdSource appIdSource; public string publisherPrefix; public CatalogAdMobIds android; public CatalogAdMobIds ios; public CatalogTestDevices testDevices; public CatalogTestSuite testSuite; public string debugger; }
    [Serializable] public sealed class CatalogSteps { public string[] en; public string[] tr; }

    [Serializable]
    public sealed class CatalogProvider
    {
        public string id; public string name; public string role; public string when; public string registration; public string docs;
        public string[] policies; public CatalogFamilies familiesSelfCertified; public bool childDirectedAllowed;
        public CatalogSdk sdk; public CatalogTestData testData; public CatalogSteps steps;

        public AdProviderKind Kind
        {
            get
            {
                switch (id) { case "levelplay": return AdProviderKind.LevelPlay; case "max": return AdProviderKind.Max; case "admob": return AdProviderKind.AdMob; default: return AdProviderKind.None; }
            }
        }
    }

    [Serializable]
    public sealed class CatalogNetwork
    {
        public string id; public string name; public string when; public string registration; public string[] policies;
        public CatalogFamilies familiesSelfCertified; public bool childDirectedAllowed; public bool appAdsTxtRequired; public string note;
    }

    [Serializable] public sealed class CatalogGooglePlay { public string adsPolicy; public string familiesPolicy; public string familiesSelfCertifiedSdks; public string advertisingId; public string appContentDeclarations; public string adIdPermission; public int adIdPermissionRequiredFromTargetSdk; }
    [Serializable] public sealed class CatalogCommonRules { public string[] en; public string[] tr; }

    [Serializable]
    public sealed class PlatformCatalog
    {
        public string readDate; public string note;
        public CatalogProvider[] providers; public CatalogNetwork[] networks;
        public CatalogGooglePlay googlePlay; public CatalogCommonRules commonRules;

        public const string FileName = "PlatformCatalog.json";
        private static PlatformCatalog _cached;

        public static string Path
        {
            get
            {
                var package = PackageInfo.FindForAssembly(typeof(PlatformCatalog).Assembly);
                string folder = package != null ? package.resolvedPath : System.IO.Path.GetFullPath("Packages/com.nexus.ads");
                return System.IO.Path.Combine(folder, "Editor", FileName);
            }
        }

        public static PlatformCatalog Load(bool fresh = false)
        {
            if (_cached != null && !fresh) return _cached;
            return _cached = Parse(File.ReadAllText(Path));
        }

        public static PlatformCatalog Parse(string json)
        {
            var catalog = JsonUtility.FromJson<PlatformCatalog>(json);
            if (catalog == null || catalog.providers == null) throw new InvalidDataException(FileName + " did not parse");
            return catalog;
        }

        public CatalogProvider Provider(AdProviderKind kind)
        {
            foreach (var p in providers) if (p.Kind == kind) return p;
            return null;
        }

        public CatalogNetwork Network(string id)
        {
            foreach (var n in networks) if (n.id == id) return n;
            return null;
        }

        /// <summary>Google's sample keys for a platform, as the catalog records them.</summary>
        public PlatformKeys SampleAdMobKeys(AdPlatform platform)
        {
            var admob = Provider(AdProviderKind.AdMob);
            var ids = platform == AdPlatform.Ios ? admob.testData.ios : admob.testData.android;
            return new PlatformKeys { AppKey = ids.appId, RewardedUnitId = ids.rewarded, InterstitialUnitId = ids.interstitial, BannerUnitId = ids.banner };
        }
    }
}
