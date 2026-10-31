using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace NexusVerify
{
    /// <summary>
    /// Headless player-build targets for Mono + IL2CPP compile verification of the
    /// Nexus demo project (Unity 6000.6.7f1). Invoked from the Unity CLI via
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
            BuildOptions buildOptions = BuildOptions.None, ManagedStrippingLevel? stripping = null, string[] defines = null)
        {
            // Restore the pre-existing backend when the batch finishes (a stripping build still leaves
            // its previous level written explicitly; see the finally block). NamedBuildTarget is the
            // non-obsolete Unity 6 API (the BuildTargetGroup overloads are CS0618).
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            // Every development player holds Assets/Scripts/Verify/DeviceProbe.cs and no release player does: the
            // define replaces DEVELOPMENT_BUILD, which Unity 6000.6 deprecates (#140).
            if ((buildOptions & BuildOptions.Development) != 0)
            {
                var all = new string[(defines?.Length ?? 0) + 1];
                defines?.CopyTo(all, 0);
                all[all.Length - 1] = "NEXUS_DEVICE_PROBE";
                defines = all;
            }
            var previous = PlayerSettings.GetScriptingBackend(namedTarget);
            // BuildPlayer switches the editor's active build target and leaves it there, which changes
            // which scripting defines (e.g. the Standalone NEXUS_DEBUG) later editor runs compile with.
            var previousTarget = EditorUserBuildSettings.activeBuildTarget;
            var previousGroup = BuildPipeline.GetBuildTargetGroup(previousTarget);
            var previousStripping = PlayerSettings.GetManagedStrippingLevel(namedTarget);
            PlayerSettings.SetScriptingBackend(namedTarget, backend);
            if (stripping.HasValue) PlayerSettings.SetManagedStrippingLevel(namedTarget, stripping.Value);
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
                    extraScriptingDefines = defines,
                };

                var buildStart = DateTime.UtcNow;
                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    throw new Exception(
                        $"Build FAILED ({targetName}, {backend}): result={summary.result}, errors={summary.totalErrors}.\n" +
                        "See the -logFile output for compiler errors.");
                }
                // IL2CPP always runs UnityLinker, and the packages' link.xml files reach it only through the core's
                // NexusPackagesLinkXml, which writes this file (#110, #115).
                const string packagesLinkXml = "Library/Nexus/PackagesLink.xml";
                if (backend == ScriptingImplementation.IL2CPP && (!System.IO.File.Exists(packagesLinkXml) || System.IO.File.GetLastWriteTimeUtc(packagesLinkXml) < buildStart))
                    throw new Exception($"Build {targetName}: the linker got no Nexus package link.xml ({packagesLinkXml} was not written by this build).");

                Console.WriteLine($"[NexusVerifyBuild] {targetName} ({backend}, stripping {PlayerSettings.GetManagedStrippingLevel(namedTarget)}) succeeded: {summary.totalSize} bytes, " +
                    $"{summary.totalTime.TotalSeconds:F1}s, warnings={summary.totalWarnings}");
                WriteCommitFile(targetName);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(namedTarget, previous);
                // No API unsets the level: after a stripping build, ProjectSettings.asset holds the previous level explicitly.
                if (stripping.HasValue) PlayerSettings.SetManagedStrippingLevel(namedTarget, previousStripping);
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

        /// <summary>Android, IL2CPP, development build: holds Assets/Scripts/Verify/DeviceProbe.cs (phone checks for #77, #84 and #110).</summary>
        public static void BuildAndroidIL2CPPDevelopment()
        {
            Build("android-il2cpp-dev.apk", BuildTargetGroup.Android, BuildTarget.Android, ScriptingImplementation.IL2CPP, BuildOptions.Development);
        }

        /// <summary>
        /// The development build above at the managed stripping level given by -nexusStripping Low|Medium|High (default High;
        /// issues #110, #113), as builds/android-il2cpp-dev-&lt;level&gt;.apk. The project and the games build at the default level,
        /// Minimal, where the linker copies the package assemblies whole. Only these builds test what the packages' link.xml
        /// files preserve (handed over by NexusPackagesLinkXml): tools/unity-verify/check-stripped.ps1 on
        /// Library/Bee/artifacts/Android/ManagedStripped, and DeviceProbe's "framework services" line on the phone.
        /// </summary>
        public static void BuildAndroidIL2CPPDevelopmentStripped()
        {
            var level = StrippingArgument();
            Build($"android-il2cpp-dev-{level.ToString().ToLowerInvariant()}.apk", BuildTargetGroup.Android, BuildTarget.Android,
                ScriptingImplementation.IL2CPP, BuildOptions.Development, level);
        }

        /// <summary>
        /// Android, IL2CPP, development build with NEXUS_STACK_PROBE: holds Assets/Scripts/Verify/StackProbe.cs, the phone
        /// check of the screen stack (issue #76), as builds/android-il2cpp-stackprobe.apk; tools/unity-verify/stack-probe.ps1 runs it.
        /// </summary>
        public static void BuildAndroidStackProbe()
        {
            Build("android-il2cpp-stackprobe.apk", BuildTargetGroup.Android, BuildTarget.Android, ScriptingImplementation.IL2CPP,
                BuildOptions.Development, defines: new[] { "NEXUS_STACK_PROBE" });
        }

        private static ManagedStrippingLevel StrippingArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-nexusStripping");
            if (index < 0) return ManagedStrippingLevel.High;
            if (index + 1 < args.Length
                && Enum.TryParse(args[index + 1], true, out ManagedStrippingLevel level)
                && level is ManagedStrippingLevel.Low or ManagedStrippingLevel.Medium or ManagedStrippingLevel.High)
                return level;
            throw new ArgumentException("-nexusStripping takes Low, Medium or High.");
        }
    }
}
