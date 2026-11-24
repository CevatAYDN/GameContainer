using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// The untracked <c>NexusAdsIds.local.json</c> at the Unity project root: the real ad ids (AdMob app and unit ids,
    /// LevelPlay app keys, test device ids) that never live in git. The tracked <c>NexusAdsSettings.asset</c> keeps those
    /// fields empty; the editor overlays them in memory, builds bake them in through <c>AdsIdsOverlay</c>.
    /// </summary>
    [Serializable]
    public sealed class AdsIdsFile
    {
        public const string FileName = "NexusAdsIds.local.json";

        public ProviderTab Test = new ProviderTab();
        public ProviderTab Release = new ProviderTab();
        public List<string> TestDeviceIds = new List<string>();

        /// <summary>Tests point this at a temp file; null means <see cref="DefaultPath"/>.</summary>
        public static string PathOverride;

        public static string DefaultPath => System.IO.Path.Combine(Directory.GetParent(Application.dataPath).FullName, FileName);
        public static string Path => PathOverride ?? DefaultPath;

        public static AdsIdsFile Read(string path)
        {
            if (!File.Exists(path)) return null;
            return JsonUtility.FromJson<AdsIdsFile>(File.ReadAllText(path));
        }

        public static AdsIdsFile Capture(AdsSettings s)
        {
            var f = new AdsIdsFile();
            f.Test = JsonUtility.FromJson<ProviderTab>(JsonUtility.ToJson(s.Test));
            f.Release = JsonUtility.FromJson<ProviderTab>(JsonUtility.ToJson(s.Release));
            f.TestDeviceIds = new List<string>(s.TestDeviceIds);
            return f;
        }

        public void Write(string path) => File.WriteAllText(path, JsonUtility.ToJson(this, true));

        /// <summary>Fills only the EMPTY fields of <paramref name="s"/>; a non-empty field is kept and reported through <paramref name="warn"/>.</summary>
        public void FillEmpty(AdsSettings s, Action<string> warn = null)
        {
            FillTab(s.Test, Test, "Test", warn);
            FillTab(s.Release, Release, "Release", warn);
            bool tracked = false;
            foreach (var id in s.TestDeviceIds) if (!string.IsNullOrWhiteSpace(id)) tracked = true;
            if (tracked) warn?.Invoke("NexusAdsSettings: TestDeviceIds is not empty in the tracked asset; keeping it (ids belong in " + FileName + ").");
            else if (TestDeviceIds != null && TestDeviceIds.Count > 0) s.TestDeviceIds = new List<string>(TestDeviceIds);
        }

        /// <summary>Blanks every id field of the asset (the fields <see cref="FillEmpty"/> fills).</summary>
        public static void Scrub(AdsSettings s)
        {
            foreach (var tab in new[] { s.Test, s.Release })
            foreach (var p in new[] { AdProviderKind.LevelPlay, AdProviderKind.Max, AdProviderKind.AdMob })
            foreach (var plat in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                var k = tab.For(p).For(plat);
                k.AppKey = k.RewardedUnitId = k.InterstitialUnitId = k.BannerUnitId = "";
            }
            s.TestDeviceIds = new List<string>();
        }

        static void FillTab(ProviderTab dst, ProviderTab src, string tab, Action<string> warn)
        {
            if (src == null) return;
            foreach (var p in new[] { AdProviderKind.LevelPlay, AdProviderKind.Max, AdProviderKind.AdMob })
            foreach (var plat in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                string at = tab + "." + p + "." + plat + ".";
                var d = dst.For(p).For(plat);
                var f = src.For(p)?.For(plat);
                if (f == null) continue;
                Fill(ref d.AppKey, f.AppKey, at + "AppKey", warn);
                Fill(ref d.RewardedUnitId, f.RewardedUnitId, at + "RewardedUnitId", warn);
                Fill(ref d.InterstitialUnitId, f.InterstitialUnitId, at + "InterstitialUnitId", warn);
                Fill(ref d.BannerUnitId, f.BannerUnitId, at + "BannerUnitId", warn);
            }
        }

        static void Fill(ref string dst, string src, string name, Action<string> warn)
        {
            if (!string.IsNullOrWhiteSpace(dst)) { warn?.Invoke("NexusAdsSettings: " + name + " is not empty in the tracked asset; keeping it (ids belong in " + FileName + ")."); return; }
            if (!string.IsNullOrWhiteSpace(src)) dst = src;
        }

#if UNITY_EDITOR
        /// <summary>Editor Play Mode: a copy of the asset with its empty ids filled from the local file; the asset itself is never touched.</summary>
        internal static AdsSettings OverlayInMemory(AdsSettings asset)
        {
            var file = Read(Path);
            if (file == null) return asset;
            var copy = UnityEngine.Object.Instantiate(asset);
            copy.name = asset.name;
            copy.hideFlags = HideFlags.DontSave;
            file.FillEmpty(copy); // no warning: the tracked asset is already warned about by the build overlay
            return copy;
        }
#endif
    }
}
