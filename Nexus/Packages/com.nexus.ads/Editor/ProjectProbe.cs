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
        /// <summary>The Nexus provider package (com.nexus.ads.levelplay, ...) is compiled in: its provider type exists, not only its assembly.</summary>
        bool ProviderPackageInstalled(AdProviderKind kind);
        /// <summary>
        /// Why the provider is not compiled although its Nexus package and every package its asmdef's versionDefines name are in
        /// the project (a package below the asmdef's expression leaves the define off and the assembly empty, issue #143), or null.
        /// </summary>
        string ProviderCompiledOut(AdProviderKind kind);
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
        // The type each Nexus provider package compiles and the asmdef whose versionDefines gate it: an assembly can exist and be
        // empty (issue #143), so the type is what tells
        private static readonly Dictionary<AdProviderKind, (string package, string type, string asmdef)> ProviderSource = new Dictionary<AdProviderKind, (string, string, string)>
        {
            { AdProviderKind.LevelPlay, ("com.nexus.ads.levelplay", "Nexus.Ads.LevelPlay.LevelPlayProvider, Nexus.Ads.LevelPlay", "Runtime/com.nexus.ads.levelplay.asmdef") },
            { AdProviderKind.AdMob, ("com.nexus.ads.admob", "Nexus.Ads.AdMob.AdMobProvider, Nexus.Ads.AdMob", "Runtime/Provider/Nexus.Ads.AdMob.asmdef") },
        };
        // The ad system domain each network's app-ads.txt line starts with, from the network's own app-ads.txt page (read
        // 2026-10-31, issue #124). Unity Ads: unity.com is what its dashboard gives (Wickling's hosted file), unity3d.com the
        // older documentation's line.
        private static readonly (string id, string[] keywords, string[] appAdsDomains)[] KnownNetworks =
        {
            ("mintegral", new[] { "mintegral" }, new[] { "mintegral.com" }), ("pangle", new[] { "pangle" }, new[] { "pangleglobal.com" }),
            ("chartboost", new[] { "chartboost" }, new[] { "chartboost.com" }), ("meta", new[] { "facebook", "audiencenetwork", "audience-network" }, new[] { "facebook.com" }),
            ("inmobi", new[] { "inmobi" }, new[] { "inmobi.com" }), ("liftoff", new[] { "vungle", "liftoff" }, new[] { "vungle.com" }),
            ("dtexchange", new[] { "fyber", "dtexchange" }, new[] { "fyber.com" }), ("unityads", new[] { "unityads", "unity-ads" }, new[] { "unity.com", "unity3d.com" }),
            ("admob", new[] { "admob", "googleads" }, new[] { "google.com" }),
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
            foreach (var network in KnownNetworks) if (Mentions(network.keywords)) _networks.Add(network.id);
            if (Directory.Exists("Assets"))
                foreach (var file in Directory.GetFiles("Assets", "link.xml", SearchOption.AllDirectories)) _linkXml.Add(File.ReadAllText(file));
            CustomAndroidManifest = File.Exists("Assets/Plugins/Android/AndroidManifest.xml") ? File.ReadAllText("Assets/Plugins/Android/AndroidManifest.xml") : null;
            AndroidTargetSdk = (int)PlayerSettings.Android.targetSdkVersion;
            AndroidMinSdk = (int)PlayerSettings.Android.minSdkVersion;
        }

        public bool SdkInstalled(AdProviderKind kind) => SdkViaUpm(kind) || (SdkAssemblies.TryGetValue(kind, out var names) && Array.Exists(names, _assemblies.Contains));
        public bool SdkViaUpm(AdProviderKind kind) => UpmPackage.TryGetValue(kind, out var name) && _manifestPackages.Contains(name);
        public bool ProviderPackageInstalled(AdProviderKind kind) => ProviderSource.TryGetValue(kind, out var source) && Type.GetType(source.type) != null;
        public string ProviderCompiledOut(AdProviderKind kind)
            => ProviderSource.TryGetValue(kind, out var source) && _packages.ContainsKey(source.package) && Type.GetType(source.type) == null
                ? CompiledOutReason(source.package, PackageFileText(source.package, source.asmdef), PackageVersion)
                : null;

        [Serializable] private sealed class Asmdef { public string name; public VersionDefine[] versionDefines; }
        [Serializable] private sealed class VersionDefine { public string name; public string expression; public string define; }

        /// <summary>
        /// The sentence for a provider package whose provider type is missing: each versionDefines symbol with the package and
        /// expression it needs and the version installed. Null when a package the asmdef names is not installed, so the provider is
        /// inert by design. Unity reads a bare version as that version or newer, brackets as an interval.
        /// </summary>
        public static string CompiledOutReason(string package, string asmdefJson, Func<string, string> installedVersion)
        {
            var asmdef = string.IsNullOrEmpty(asmdefJson) ? null : JsonUtility.FromJson<Asmdef>(asmdefJson);
            if (asmdef?.versionDefines == null || asmdef.versionDefines.Length == 0)
                return package + " is in the project but its provider is not compiled, and its asmdef names no versionDefines that explain it";
            var needs = new List<string>();
            foreach (var d in asmdef.versionDefines)
            {
                string installed = installedVersion(d.name);
                if (installed == null) return null;
                bool interval = d.expression.StartsWith("[", StringComparison.Ordinal) || d.expression.StartsWith("(", StringComparison.Ordinal);
                needs.Add(d.define + " needs " + d.name + (interval ? " in " + d.expression : " " + d.expression + " or newer") + ", the project has " + installed);
            }
            return package + " is in the project but " + asmdef.name + " compiled without its provider (" + string.Join("; ", needs) + ")";
        }
        public IReadOnlyList<string> DetectedNetworks => _networks;
        /// <summary>The ad system domains a detected network's app-ads.txt line starts with; empty for an id the probe does not detect.</summary>
        public static IReadOnlyList<string> AppAdsDomains(string networkId)
        {
            foreach (var network in KnownNetworks) if (string.Equals(network.id, networkId, StringComparison.OrdinalIgnoreCase)) return network.appAdsDomains;
            return Array.Empty<string>();
        }
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
