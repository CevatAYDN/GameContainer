using System.IO;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// What the ads editor does to the project: the settings asset, the generated files, the SDK install, the key copies
    /// and Prepare Android. The Nexus window's Ads tabs (<c>Editor/Nexus</c>, compiled with <c>com.nexus.core</c>) call
    /// these; without the core, the menu items here, <c>RunCli</c>, <c>PrepareCli</c>, the pre-build hook and the plain
    /// Inspector of the settings asset remain (#193).
    /// </summary>
    public static class AdsProjectActions
    {
        public const string AssetPath = "Assets/Resources/" + AdsSettings.ResourceName + ".asset";

        /// <summary>The settings asset, created under Resources on first use.</summary>
        public static AdsSettings LoadOrCreate()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AssetPath);
            if (settings != null) return settings;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(AssetPath)));
            settings = ScriptableObject.CreateInstance<AdsSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        [MenuItem("Nexus/Ads/Generate Placements", priority = 55)]
        public static void GeneratePlacements()
        {
            var settings = LoadOrCreate();
            try { Debug.Log("[Nexus Ads] wrote " + AdPlacementsGenerator.Write(settings)); }
            catch (System.Exception ex) { Debug.LogError("[Nexus Ads] " + ex.Message); }
        }

        [MenuItem("Nexus/Ads/Write link.xml", priority = 56)]
        public static void WriteLinkXml() => Debug.Log("[Nexus Ads] wrote " + LinkXml.Write(LoadOrCreate().SelectedMediation));

        [MenuItem("Nexus/Ads/Pin family packages to one commit", priority = 57)]
        public static void PinPackages()
        {
            string manifest = File.ReadAllText("Packages/manifest.json");
            string pinned = ManifestPinning.Pin(manifest);
            if (pinned == manifest) { Debug.Log("[Nexus Ads] nothing to pin: the family packages are embedded or not git-pinned"); return; }
            File.WriteAllText("Packages/manifest.json", pinned);
            Client.Resolve();
            Debug.Log("[Nexus Ads] family packages pinned to the commit of com.nexus.ads");
        }

        /// <summary>Adds the provider's UPM package where the catalog names a registry Unity can reach; otherwise opens its releases.</summary>
        public static void InstallSdk(CatalogProvider provider)
        {
            var upm = provider.sdk?.upm;
            if (provider.Kind == AdProviderKind.LevelPlay && upm != null) { Client.Add(upm.package); Debug.Log("[Nexus Ads] adding " + upm.package + " from the Unity registry"); return; }
            if (provider.Kind == AdProviderKind.AdMob && upm != null)
            {
                // OpenUPM is a scoped registry the project may not have yet: add it, or extend its scopes, then add the package
                string manifest = File.ReadAllText("Packages/manifest.json");
                string updated = AndroidSetup.WithOpenUpmScopes(manifest, upm.scopes ?? new[] { upm.package });
                if (updated != manifest) File.WriteAllText("Packages/manifest.json", updated);
                Client.Add(upm.package);
                Debug.Log("[Nexus Ads] adding " + upm.package + " from OpenUPM (11.6.0 brings Unity's External Dependency Manager as its dependency)");
                return;
            }
            // MAX: AppLovin's installer package writes its own scoped registry; the catalog points at the releases.
            Application.OpenURL(provider.sdk?.unitypackage ?? provider.docs);
        }

        /// <summary>
        /// With AdMob selected, writes Google's sample ids into each platform of the Test tab that has no key at all; a
        /// platform with any key is left as it is. True when something was written.
        /// </summary>
        public static bool FillEmptyAdMobTestTab(AdsSettings settings, PlatformCatalog catalog)
        {
            if (settings.SelectedMediation != AdProviderKind.AdMob) return false;
            bool changed = false;
            foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                var target = settings.Test.AdMob.For(platform);
                if (!string.IsNullOrEmpty(target.AppKey) || !string.IsNullOrEmpty(target.RewardedUnitId) || !string.IsNullOrEmpty(target.InterstitialUnitId) || !string.IsNullOrEmpty(target.BannerUnitId)) continue;
                if (!changed) Undo.RecordObject(settings, "Fill AdMob test data");
                Copy(catalog.SampleAdMobKeys(platform), target);
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(settings);
            return changed;
        }

        /// <summary>Writes Google's sample ids over the AdMob Test tab of one platform.</summary>
        public static void ResetAdMobTestData(AdsSettings settings, PlatformCatalog catalog, AdPlatform platform)
        {
            Undo.RecordObject(settings, "Reset AdMob test data");
            Copy(catalog.SampleAdMobKeys(platform), settings.Test.AdMob.For(platform));
            EditorUtility.SetDirty(settings);
        }

        /// <summary>Copies a mediation's Release keys of one platform to its Test tab (a mediation without published test keys).</summary>
        public static void CopyReleaseToTest(AdsSettings settings, AdProviderKind kind, AdPlatform platform)
        {
            Undo.RecordObject(settings, "Copy Release keys to Test");
            Copy(settings.Release.For(kind).For(platform), settings.Test.For(kind).For(platform));
            EditorUtility.SetDirty(settings);
        }

        /// <summary>Performs the selected mediation's Android setup for a tab (<see cref="AndroidSetup.Apply"/>) and logs each step.</summary>
        public static void PrepareAndroid(AdsSettings settings, AdsValidator.Tab tab, PlatformCatalog catalog)
        {
            try
            {
                foreach (var line in AndroidSetup.Apply(settings, tab, catalog).Log) Debug.Log("[Nexus Ads] Prepare Android: " + line);
            }
            catch (System.Exception e) { Debug.LogError("[Nexus Ads] Prepare Android stopped: " + e.Message + " (offline? the native SDK file comes from the mediation's server)"); }
        }

        /// <summary>Asks where to save, then writes the settings' app-ads.txt lines there.</summary>
        public static void WriteAppAdsTxt(AdsSettings settings)
        {
            string path = EditorUtility.SaveFilePanel("Save app-ads.txt", "", "app-ads.txt", "txt");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, AppAdsTxt.Render(settings.AppAdsTxtLines));
        }

        private static void Copy(PlatformKeys from, PlatformKeys to)
        {
            to.AppKey = from.AppKey;
            to.RewardedUnitId = from.RewardedUnitId;
            to.InterstitialUnitId = from.InterstitialUnitId;
            to.BannerUnitId = from.BannerUnitId;
        }
    }
}
