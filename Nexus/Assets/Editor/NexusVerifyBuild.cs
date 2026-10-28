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
    /// rules), generic dispatch preservation, and AOT limitations (no JIT: the runtime's
    /// Expression.Compile in CommandRegistry and DynamicMethod in CompiledAccessorEmitter,
    /// which NexusDI uses, sit behind ENABLE_IL2CPP / UNITY_AOT guards). A green IL2CPP build is the strongest single signal that the runtime
    /// survives a real device pipeline.
    /// </summary>
    public static class NexusVerifyBuild
    {
        private static void Build(string targetName, BuildTargetGroup group, BuildTarget target, ScriptingImplementation backend,
            BuildOptions buildOptions = BuildOptions.None)
        {
            // Restore the pre-existing backend when the batch finishes so this
            // verification never pollutes ProjectSettings.asset. NamedBuildTarget is the
            // non-obsolete Unity 6 API (the BuildTargetGroup overloads are CS0618).
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            var previous = PlayerSettings.GetScriptingBackend(namedTarget);
            // BuildPlayer switches the editor's active build target and leaves it there, which changes
            // which scripting defines (e.g. the Standalone NEXUS_DEBUG) later editor runs compile with.
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);
            PlayerSettings.SetScriptingBackend(namedTarget, backend);
            try
            {
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/NexusStarter.unity" },
                    // A Windows player's path names its executable; each gets its own folder, or the IL2CPP
                    // build refuses the folder the Mono build wrote (issue #102).
                    locationPathName = target == BuildTarget.StandaloneWindows64 ? $"builds/{targetName}/{targetName}.exe" : $"builds/{targetName}",
                    target = target,
                    targetGroup = group,
                    options = buildOptions,
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
                WriteCommitFile(targetName);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(namedTarget, previous);
                // The build saved the settings with its backend; without this a failed build exits with it on disk (issue #102).
                AssetDatabase.SaveAssets();
                if (EditorUserBuildSettings.activeBuildTarget != previousTarget)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(previousGroup, previousTarget);
            }
        }

        /// <summary>
        /// Writes the commit the player was built from to builds/&lt;name&gt;.commit, so tools/device-acceptance
        /// names the player's commit rather than the HEAD it runs at (issue #105). Nothing is written when git fails.
        /// </summary>
        private static void WriteCommitFile(string targetName)
        {
            string path = $"builds/{targetName}.commit";
            System.IO.File.Delete(path); // an older build's file must not name this player
            string head = Git("rev-parse HEAD");
            if (string.IsNullOrEmpty(head)) return;
            if (!string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no -- Packages Assets")))
                head += " (Packages or Assets modified)";
            System.IO.File.WriteAllText(path, head + "\n");
        }

        private static string Git(string arguments)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(start);
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 ? output : null;
            }
            catch (Exception)
            {
                return null;
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

        /// <summary>Android, IL2CPP, development build: holds Assets/Scripts/Verify/DeviceProbe.cs (phone checks for #77 and #84).</summary>
        public static void BuildAndroidIL2CPPDevelopment()
        {
            Build("android-il2cpp-dev.apk", BuildTargetGroup.Android, BuildTarget.Android, ScriptingImplementation.IL2CPP, BuildOptions.Development);
        }

        /// <summary>
        /// Packs the embedded package into a UPM tarball at the folder named by NEXUS_PACK_OUTPUT
        /// (tools/nexus-consumer uses it to verify a fresh project that installs the archive).
        /// Package Manager requests complete on the editor loop, so the method polls from
        /// EditorApplication.update and exits the editor itself; run it without -quit.
        /// </summary>
        public static void PackPackage()
        {
            string output = Environment.GetEnvironmentVariable("NEXUS_PACK_OUTPUT");
            if (string.IsNullOrEmpty(output)) throw new Exception("NEXUS_PACK_OUTPUT is required.");
            System.IO.Directory.CreateDirectory(output);
            var request = UnityEditor.PackageManager.Client.Pack("Packages/com.nexus.core", output);
            void Poll()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Poll;
                if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                {
                    Console.WriteLine("[NexusVerifyBuild] packed " + request.Result.tarballPath);
                    EditorApplication.Exit(0);
                }
                else
                {
                    Console.WriteLine("[NexusVerifyBuild] pack FAILED: " + request.Error?.message);
                    EditorApplication.Exit(2);
                }
            }
            EditorApplication.update += Poll;
        }
    }
}
