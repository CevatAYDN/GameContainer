using System;
using System.IO;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// Real ad ids live in the untracked <c>NexusAdsIds.local.json</c> (project root), never in git. A build wraps itself in
    /// <see cref="Apply(bool)"/>: the asset's bytes are backed up, its empty fields filled from the file, and the exact bytes restored on
    /// Dispose. A crashed build is restored by the [InitializeOnLoad] recovery from <c>Library/NexusAdsIds.backup</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class AdsIdsOverlay
    {
        public static readonly string BackupPath = Path.Combine("Library", "NexusAdsIds.backup");
        static string BackupPathFile => BackupPath + ".path";
        static int _depth;

        static AdsIdsOverlay() => EditorApplication.delayCall += () => { if (!IsActive && !BuildPipeline.isBuildingPlayer) RestoreFromBackup(); };

        /// <summary>True while an <see cref="Apply(bool)"/> scope is open.</summary>
        public static bool IsActive => _depth > 0;

        static string AssetPathOf()
        {
            var asset = Resources.Load<AdsSettings>(AdsSettings.ResourceName);
            return asset == null ? null : AssetDatabase.GetAssetPath(asset);
        }

        /// <summary>Writes the current asset's ids to <c>NexusAdsIds.local.json</c> (once per game, before <see cref="ScrubAsset()"/>).</summary>
        [MenuItem("Nexus/Ads/Export Local Ids File")]
        public static void ExportLocalFile() => ExportLocalFile(null, null);

        public static void ExportLocalFile(string assetPath, string localPath)
        {
            var settings = LoadAsset(assetPath);
            AdsIdsFile.Capture(settings).Write(localPath ?? AdsIdsFile.Path);
            Debug.Log("[Nexus Ads] Wrote " + (localPath ?? AdsIdsFile.Path) + ". Keep it untracked (.gitignore), then run Nexus > Ads > Scrub Asset Ids.");
        }

        /// <summary>Blanks the id fields of the asset. Refuses unless the local file exists, so ids are never lost.</summary>
        [MenuItem("Nexus/Ads/Scrub Asset Ids")]
        public static void ScrubAsset() => ScrubAsset(null, null);

        public static void ScrubAsset(string assetPath, string localPath)
        {
            if (!File.Exists(localPath ?? AdsIdsFile.Path)) throw new InvalidOperationException("Run Export Local Ids File first: " + AdsIdsFile.FileName + " does not exist.");
            var settings = LoadAsset(assetPath);
            AdsIdsFile.Scrub(settings);
            Save(settings);
        }

        static AdsSettings LoadAsset(string assetPath)
        {
            assetPath = assetPath ?? AssetPathOf();
            var settings = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<AdsSettings>(assetPath);
            if (settings == null) throw new InvalidOperationException("No Resources/" + AdsSettings.ResourceName + " asset in the project.");
            return settings;
        }

        static void Save(AdsSettings settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(settings), ImportAssetOptions.ForceUpdate);
        }

        /// <summary>
        /// See the class summary. Nested calls are no-ops (refcount). Throws <see cref="BuildFailedException"/> when
        /// <paramref name="requireRelease"/> and the file or the selected mediation's Release app key is missing.
        /// </summary>
        public static IDisposable Apply(bool requireRelease) => Apply(requireRelease, EditorUserBuildSettings.activeBuildTarget, null, null);

        public static IDisposable Apply(bool requireRelease, BuildTarget target, string assetPath, string localPath)
        {
            if (_depth > 0) { _depth++; return new Scope(true); }
            localPath = localPath ?? AdsIdsFile.Path;
            assetPath = assetPath ?? AssetPathOf();
            if (string.IsNullOrEmpty(assetPath)) return new Scope(false); // no ads settings in this project
            RestoreFromBackup();
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(assetPath);
            bool needsIds = settings.SelectedMediation != AdProviderKind.None && !settings.AdFreeRelease;
            var file = AdsIdsFile.Read(localPath);
            if (file == null)
            {
                if (requireRelease && needsIds) throw new BuildFailedException("[Nexus Ads] A release build needs " + localPath + " (the real ad ids; create it with Nexus > Ads > Export Local Ids File), but it does not exist.");
                return new Scope(false);
            }

            File.WriteAllBytes(BackupPath, File.ReadAllBytes(assetPath));
            File.WriteAllText(BackupPathFile, assetPath);
            file.FillEmpty(settings, message => Debug.LogWarning("[Nexus Ads] " + message));
            Save(settings);
            if (requireRelease && needsIds && (target == BuildTarget.Android || target == BuildTarget.iOS))
            {
                var platform = target == BuildTarget.iOS ? AdPlatform.Ios : AdPlatform.Android;
                if (string.IsNullOrWhiteSpace(settings.Release.For(settings.SelectedMediation).For(platform).AppKey))
                {
                    RestoreFromBackup();
                    throw new BuildFailedException("[Nexus Ads] " + localPath + " has no Release " + settings.SelectedMediation + " " + platform + " AppKey; a release build cannot ship without it.");
                }
            }
            _depth = 1;
            return new Scope(true);
        }

        /// <summary>Puts the backed-up asset bytes back and deletes the backup; false when there is none. Used by Dispose and crash recovery.</summary>
        public static bool RestoreFromBackup()
        {
            _depth = 0; // a restored asset ends every scope
            if (!File.Exists(BackupPath) || !File.Exists(BackupPathFile)) return false;
            string path = File.ReadAllText(BackupPathFile);
            File.WriteAllBytes(path, File.ReadAllBytes(BackupPath));
            File.Delete(BackupPath);
            File.Delete(BackupPathFile);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return true;
        }

        sealed class Scope : IDisposable
        {
            readonly bool _counted; bool _done;
            public Scope(bool counted) { _counted = counted; }
            public void Dispose()
            {
                if (_done || !_counted) return;
                _done = true;
                if (--_depth <= 0) { _depth = 0; RestoreFromBackup(); }
            }
        }
    }

    /// <summary>
    /// Menu and UI builds: applies the overlay before <see cref="AdsBuildGuard"/> (-900) validates, disposes it after the build.
    /// Both are no-ops while a caller holds its own <see cref="AdsIdsOverlay.Apply(bool)"/> scope.
    /// </summary>
    public sealed class AdsIdsBuildHook : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        static IDisposable _scope;
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (AdsIdsOverlay.IsActive) return;
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            _scope = AdsIdsOverlay.Apply(!development, report.summary.platform, null, null);
            // A failed BuildPlayer can skip the postprocess; restore once the editor is idle again.
            EditorApplication.delayCall += () => { if (!BuildPipeline.isBuildingPlayer) Release(); };
        }

        public void OnPostprocessBuild(BuildReport report) => Release();

        static void Release()
        {
            _scope?.Dispose();
            _scope = null;
        }
    }
}
