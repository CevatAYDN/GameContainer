#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// The iOS App Store kit's player post-process (#436), applied to the exported Xcode project when the game has an
    /// <see cref="IosKitSettings"/> asset: Info.plist keys (<see cref="InfoPlistRules"/>), per-language InfoPlist.strings, the privacy
    /// manifest written from the declared list and registered in the project, and the Pods minimum iOS. An invalid manifest fails the
    /// build. Compiled with the iOS build target active (<c>UNITY_IOS</c>); not compiled or run when it was written, see the changelog.
    /// </summary>
    public static class IosPostProcess
    {
        // After the packages' own steps (LevelPlay 900) and before the games' (999).
        private const int Order = 950;

        [PostProcessBuild(Order)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;
            var settings = IosKitSettings.Find();
            if (settings == null)
            {
                Debug.Log("[Nexus iOS] No IosKitSettings asset in the project: the iOS App Store kit does nothing (Assets > Create > Nexus > iOS App Store Kit).");
                return;
            }

            // The manifest first: nothing is written when the declaration is invalid.
            var spec = settings.ToManifestSpec();
            var problems = PrivacyManifest.Validate(spec, settings.declaresTracking);
            if (problems.Count > 0)
            {
                throw new BuildFailedException("[Nexus iOS] PrivacyInfo.xcprivacy declaration is invalid (GameContainer #436):\n - " + string.Join("\n - ", problems));
            }

            string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
            var plist = InfoPlistRules.Apply(File.ReadAllText(plistPath), settings.ToInfoPlistOptions());
            if (plist.Errors.Count > 0)
            {
                throw new BuildFailedException("[Nexus iOS] Info.plist: " + string.Join(" ", plist.Errors));
            }
            File.WriteAllText(plistPath, plist.Xml);
            foreach (string change in plist.Changes) Debug.Log("[Nexus iOS] Info.plist: " + change);

            // Always overwrite: an appended Xcode export would otherwise keep a stale manifest (ITMS-91054).
            File.WriteAllText(Path.Combine(pathToBuiltProject, "PrivacyInfo.xcprivacy"), PrivacyManifest.ToXml(spec));

            string projPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string mainTarget = proj.GetUnityMainTargetGuid();
            if (proj.FindFileGuidByProjectPath("PrivacyInfo.xcprivacy") == null)
            {
                string fileGuid = proj.AddFile("PrivacyInfo.xcprivacy", "PrivacyInfo.xcprivacy", PBXSourceTree.Source);
                proj.AddFileToBuild(mainTarget, fileGuid);
            }

            foreach (var language in settings.languages)
            {
                if (string.IsNullOrEmpty(language.displayName)) continue;
                string folder = language.code + ".lproj";
                Directory.CreateDirectory(Path.Combine(pathToBuiltProject, folder));
                File.WriteAllText(Path.Combine(pathToBuiltProject, folder, "InfoPlist.strings"), InfoPlistRules.InfoPlistStrings("CFBundleDisplayName", language.displayName));
                if (proj.FindFileGuidByProjectPath(folder) == null)
                {
                    string folderGuid = proj.AddFolderReference(folder, folder, PBXSourceTree.Source);
                    proj.AddFileToBuild(mainTarget, folderGuid);
                }
            }
            proj.WriteToFile(projPath);

            string podfilePath = Path.Combine(pathToBuiltProject, "Podfile");
            if (File.Exists(podfilePath) && !string.IsNullOrEmpty(settings.podsMinimumIos))
            {
                string patched = PodfilePatch.EnsureMinimumIos(File.ReadAllText(podfilePath), settings.podsMinimumIos, out bool changed);
                if (changed) File.WriteAllText(podfilePath, patched);
            }

            Debug.Log("[Nexus iOS] Info.plist, PrivacyInfo.xcprivacy (" + spec.DataTypes.Count + " data types, " + spec.AccessedApis.Count + " required-reason APIs) and Pods set for the App Store (#436).");
        }
    }
}
#endif
