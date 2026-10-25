using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// What the validator and the resolver need to know about the project. The real probe reads the
    /// project; tests hand in a fake, so every check is a pure function over settings and probe.
    /// </summary>
    public interface IProjectProbe
    {
        /// <summary>The provider's SDK is in the project, by UPM package or by a .unitypackage import.</summary>
        bool SdkInstalled(AdProviderKind kind);
        /// <summary>The SDK came as a UPM package (versionDefines works) rather than a .unitypackage import.</summary>
        bool SdkViaUpm(AdProviderKind kind);
        /// <summary>The Nexus provider package (com.nexus.ads.levelplay, ...) is compiled in.</summary>
        bool ProviderPackageInstalled(AdProviderKind kind);
        /// <summary>Lower-case ids of the network adapters found (mintegral, pangle, chartboost, meta, inmobi, liftoff, dtexchange, unityads, admob); a heuristic.</summary>
        IReadOnlyList<string> DetectedNetworks { get; }
        /// <summary>A dependency resolver that works is in the project (a disabled stub does not count).</summary>
        bool ExternalDependencyManagerInstalled { get; }
        /// <summary>com.google.external-dependency-manager is a disabled stub (its folder carries .edm_dummy): it only satisfies another package's dependency and shadows the real package of that name.</summary>
        bool DisabledEdmStub { get; }
        /// <summary>Text of Assets/Plugins/Android/AndroidManifest.xml, or null when the project has no custom manifest.</summary>
        string CustomAndroidManifest { get; }
        int AndroidTargetSdk { get; }
        int AndroidMinSdk { get; }
        /// <summary>Texts of every link.xml under Assets.</summary>
        IReadOnlyList<string> LinkXmlTexts { get; }
        /// <summary>Packages/manifest.json text.</summary>
        string ConsumerManifestJson { get; }
        /// <summary>The app id in the Google Mobile Ads settings asset for the platform, or null when the asset is absent.</summary>
        string AdMobSettingsAppId(AdPlatform platform);
        /// <summary>The installed version of a package, or null when it is not in the project.</summary>
        string PackageVersion(string package);
        /// <summary>Text of a file inside an installed package (path relative to the package root), or null.</summary>
        string PackageFileText(string package, string relativePath);
        /// <summary>Text of a project file (Assets/...), or null when it does not exist.</summary>
        string ProjectFileText(string path);
    }

    /// <summary>The probe over the open project. Detection of SDKs and adapters is by package name, assembly name and well-known folders; the messages say so.</summary>
    public sealed class UnityProjectProbe : IProjectProbe
    {
        private static readonly Dictionary<AdProviderKind, string> UpmPackage = new Dictionary<AdProviderKind, string>
        {
            { AdProviderKind.LevelPlay, "com.unity.services.levelplay" }, { AdProviderKind.Max, "com.applovin.mediation.ads" }, { AdProviderKind.AdMob, "com.google.ads.mobile" },
        };
        private static readonly Dictionary<AdProviderKind, string[]> SdkAssemblies = new Dictionary<AdProviderKind, string[]>
        {
            { AdProviderKind.LevelPlay, new[] { "Unity.Services.LevelPlay", "IronSource" } }, { AdProviderKind.Max, new[] { "AppLovin.MAX", "MaxSdk" } }, { AdProviderKind.AdMob, new[] { "GoogleMobileAds" } },
        };
        private static readonly Dictionary<AdProviderKind, string> ProviderAssembly = new Dictionary<AdProviderKind, string>
        {
            { AdProviderKind.LevelPlay, "Nexus.Ads.LevelPlay" }, { AdProviderKind.Max, "Nexus.Ads.Max" }, { AdProviderKind.AdMob, "Nexus.Ads.AdMob" },
        };
        private static readonly (string id, string[] keywords)[] NetworkKeywords =
        {
            ("mintegral", new[] { "mintegral" }), ("pangle", new[] { "pangle" }), ("chartboost", new[] { "chartboost" }), ("meta", new[] { "facebook", "audiencenetwork", "audience-network" }),
            ("inmobi", new[] { "inmobi" }), ("liftoff", new[] { "vungle", "liftoff" }), ("dtexchange", new[] { "fyber", "dtexchange" }), ("unityads", new[] { "unityads", "unity-ads" }), ("admob", new[] { "admob", "googleads" }),
        };

        private readonly HashSet<string> _manifestPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _networks = new List<string>();
        private readonly List<string> _linkXml = new List<string>();
        private readonly Dictionary<string, UnityEditor.PackageManager.PackageInfo> _packages = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>(StringComparer.OrdinalIgnoreCase);

        public UnityProjectProbe()
        {
            ConsumerManifestJson = File.Exists("Packages/manifest.json") ? File.ReadAllText("Packages/manifest.json") : "{}";
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ConsumerManifestJson, "\"(com\\.[a-z0-9.\\-]+)\"\\s*:"))
                _manifestPackages.Add(m.Groups[1].Value);
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()) { _manifestPackages.Add(package.name); _packages[package.name] = package; }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) _assemblies.Add(asm.GetName().Name);
            foreach (var pair in NetworkKeywords) if (Mentions(pair.keywords)) _networks.Add(pair.id);
            if (Directory.Exists("Assets"))
                foreach (var file in Directory.GetFiles("Assets", "link.xml", SearchOption.AllDirectories)) _linkXml.Add(File.ReadAllText(file));
            CustomAndroidManifest = File.Exists("Assets/Plugins/Android/AndroidManifest.xml") ? File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml") : null;
            AndroidTargetSdk = (int)PlayerSettings.Android.targetSdkVersion;
            AndroidMinSdk = (int)PlayerSettings.Android.minSdkVersion;
        }

        public bool SdkInstalled(AdProviderKind kind) => SdkViaUpm(kind) || (SdkAssemblies.TryGetValue(kind, out var names) && Array.Exists(names, _assemblies.Contains));
        public bool SdkViaUpm(AdProviderKind kind) => UpmPackage.TryGetValue(kind, out var name) && _manifestPackages.Contains(name);
        public bool ProviderPackageInstalled(AdProviderKind kind) => ProviderAssembly.TryGetValue(kind, out var name) && _assemblies.Contains(name);
        public IReadOnlyList<string> DetectedNetworks => _networks;
        public bool ExternalDependencyManagerInstalled => (_manifestPackages.Contains(AndroidSetup.GoogleEdmPackage) && !DisabledEdmStub) || _manifestPackages.Contains(AndroidSetup.UnityEdmPackage) || _assemblies.Contains("Google.JarResolver") || Directory.Exists("Assets/ExternalDependencyManager");
        public bool DisabledEdmStub => _packages.TryGetValue(AndroidSetup.GoogleEdmPackage, out var info) && IsDisabledEdmStub(info.resolvedPath);
        /// <summary>The marker of a disabled External Dependency Manager package (Gigawatt, Wickling and ChipCrisis embed one beside Firebase; issue #61).</summary>
        public static bool IsDisabledEdmStub(string packageRoot) => !string.IsNullOrEmpty(packageRoot) && File.Exists(Path.Combine(packageRoot, ".edm_dummy"));
        public string PackageVersion(string package) => package != null && _packages.TryGetValue(package, out var info) ? info.version : null;
        public string PackageFileText(string package, string relativePath)
        {
            if (package == null || relativePath == null || !_packages.TryGetValue(package, out var info)) return null;
            string path = Path.Combine(info.resolvedPath, relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        public string ProjectFileText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        public string CustomAndroidManifest { get; }
        public int AndroidTargetSdk { get; }
        public int AndroidMinSdk { get; }
        public IReadOnlyList<string> LinkXmlTexts => _linkXml;
        public string ConsumerManifestJson { get; }

        public string AdMobSettingsAppId(AdPlatform platform)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/GoogleMobileAds/Resources/GoogleMobileAdsSettings.asset");
            if (asset == null) return null;
            var property = new SerializedObject(asset).FindProperty(platform == AdPlatform.Ios ? "adMobIOSAppId" : "adMobAndroidAppId");
            return property?.stringValue ?? "";
        }

        // Adapter detection: UPM adapter packages (com.applovin.mediation.adapters.*, com.google.ads.mobile.mediation.*),
        // LevelPlay/MAX adapter folders under Assets, and EDM dependency xml names.
        private bool Mentions(string[] keywords)
        {
            foreach (var package in _manifestPackages)
                foreach (var k in keywords) if (package.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 && package.IndexOf("mediation", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var folder in new[] { "Assets/LevelPlay/Editor", "Assets/MaxSdk/Mediation", "Assets/GoogleMobileAds/Mediation", "Assets/IronSource/Editor" })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(folder))
                    foreach (var k in keywords) if (Path.GetFileName(entry).IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
