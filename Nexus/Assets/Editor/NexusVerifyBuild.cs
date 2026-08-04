using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace NexusVerify
{
    /// <summary>
    /// Headless player-build targets for Mono + IL2CPP compile verification of the
    /// Nexus demo project (Unity 6000.5.6f1). Invoked from the Unity CLI via
    /// -executeMethod — see tools/unity-verify/README.md for the full command list.
    ///
    /// Why both backends: Mono catches C# compile + editor/player Mono errors fast;
    /// IL2CPP additionally exercises code stripping (Runtime/link.xml preservation
    /// rules), generic dispatch preservation, and AOT limitations (no JIT, no
    /// Expression.Compile on AOT platforms — NexusDI bypasses it via ENABLE_IL2CPP
    /// guards). A green IL2CPP build is the strongest single signal that the runtime
    /// survives a real device pipeline.
    /// </summary>
    public static class NexusVerifyBuild
    {
        private static void Build(string targetName, BuildTargetGroup group, BuildTarget target, ScriptingImplementation backend)
        {
            // Restore the pre-existing backend when the batch finishes so this
            // verification never pollutes ProjectSettings.asset. NamedBuildTarget is the
            // non-obsolete Unity 6 API (the BuildTargetGroup overloads are CS0618).
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            var previous = PlayerSettings.GetScriptingBackend(namedTarget);
            PlayerSettings.SetScriptingBackend(namedTarget, backend);
            try
            {
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/NexusStarter.unity" },
                    locationPathName = $"builds/{targetName}",
                    target = target,
                    targetGroup = group,
                    options = BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    throw new Exception(
                        $"Build FAILED ({targetName}, {backend}): result={summary.result}, errors={summary.totalErrors}.\n" +
                        "See the -logFile output for compiler errors.");
                }

                Console.WriteLine($"[NexusVerifyBuild] {targetName} ({backend}) succeeded: {summary.totalSize} bytes, " +
                    $"{summary.totalTime.TotalSeconds:F1}s, warnings={summary.totalWarnings}");
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(namedTarget, previous);
            }
        }

        /// <summary>StandaloneWindows64, Mono backend — fast C# compile + player sanity check.</summary>
        public static void BuildStandaloneMono()
        {
            Build("standalone-mono", BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64, ScriptingImplementation.Mono2x);
        }

        /// <summary>StandaloneWindows64, IL2CPP backend — full code-stripping + AOT verification.</summary>
        public static void BuildStandaloneIL2CPP()
        {
            Build("standalone-il2cpp", BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64, ScriptingImplementation.IL2CPP);
        }

        /// <summary>Android, IL2CPP — optional; requires the Android SDK/NDK modules in this editor.</summary>
        public static void BuildAndroidIL2CPP()
        {
            Build("android-il2cpp", BuildTargetGroup.Android, BuildTarget.Android, ScriptingImplementation.IL2CPP);
        }
    }
}
