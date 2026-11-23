#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace Nexus.Ads.LevelPlay.Editor
{
    /// <summary>
    /// Adds <c>-ObjC</c> to the UnityFramework target's <c>OTHER_LDFLAGS</c> in the exported Xcode project, whatever resolver the game
    /// uses (#435). Without it the linker drops the LevelPlay network adapters (nothing references them; LevelPlay looks them up by
    /// class name) and every iOS ad request answers <c>509 Mediation No fill</c>. A game that already adds the flag in its own
    /// post-process keeps working: the flag is added once. Compiled with LevelPlay installed and the iOS build target active
    /// (<c>UNITY_IOS</c>); not compiled or run when it was written, see the changelog.
    /// </summary>
    public static class LevelPlayIosPostProcess
    {
        // Before the games' own post-processes (they use 999 and may read the project afterwards).
        private const int Order = 900;

        [PostProcessBuild(Order)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            string projPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string frameworkGuid = proj.GetUnityFrameworkTargetGuid();

            if (LevelPlayIosLinkerFlags.HasObjC(proj.GetBuildPropertyForAnyConfig(frameworkGuid, "OTHER_LDFLAGS")))
            {
                Debug.Log("[Nexus Ads] LevelPlay: UnityFramework already links with -ObjC.");
                return;
            }

            proj.AddBuildProperty(frameworkGuid, "OTHER_LDFLAGS", LevelPlayIosLinkerFlags.ObjC);
            proj.WriteToFile(projPath);

            // Read it back: a build that ships without the adapters is the failure this exists to stop.
            var check = new PBXProject();
            check.ReadFromFile(projPath);
            if (!LevelPlayIosLinkerFlags.HasObjC(check.GetBuildPropertyForAnyConfig(check.GetUnityFrameworkTargetGuid(), "OTHER_LDFLAGS")))
            {
                throw new BuildFailedException("[Nexus Ads] LevelPlay: could not add -ObjC to UnityFramework's OTHER_LDFLAGS in " + projPath
                    + ". Without it the linker drops the network adapters and every ad request answers 509 No fill (GameContainer #435).");
            }
            Debug.Log("[Nexus Ads] LevelPlay: added -ObjC to UnityFramework's OTHER_LDFLAGS so the network adapters stay in the binary (#435).");
        }
    }
}
#endif
