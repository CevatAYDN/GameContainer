using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Nexus.DeviceTest
{
    /// <summary>
    /// CLI build entry points for the device-test project.
    /// Invoked by Unity in batch mode:
    ///   -executeMethod Nexus.DeviceTest.BuildScript.BuildAndroid -outputPath &lt;path&gt;
    ///   -executeMethod Nexus.DeviceTest.BuildScript.BuildIOS -outputPath &lt;path&gt;
    /// </summary>
    public static class BuildScript
    {
        private const string ScenePath = "Assets/Scenes/DeviceTest.unity";

        public static void BuildAndroid()
        {
            var output = GetOutputPath("builds/nexus-soak-android.apk");
            Build(BuildTarget.Android, output);
        }

        public static void BuildIOS()
        {
            var output = GetOutputPath("builds/nexus-soak-ios");
            Build(BuildTarget.iOS, output);
        }

        private static void Build(BuildTarget target, string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("Missing -outputPath argument.");

            // Build the configured scenes; fall back to the Editor Build Settings list when
            // the canonical scene has not been authored yet.
            var scenes = System.IO.File.Exists(ScenePath)
                ? new[] { ScenePath }
                : EditorBuildSettings.scenes
                    ?.Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();

            if (scenes == null || scenes.Length == 0)
            {
                throw new Exception("No build scenes configured. Add Assets/Scenes/DeviceTest.unity (or enable scenes in Build Settings).");
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = target,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);
            var result = report.summary.result;
            if (result != BuildResult.Succeeded)
            {
                throw new Exception($"Build failed: {result} ({report.summary.totalErrors} errors)");
            }

            Debug.Log($"[BuildScript] Build succeeded: {outputPath} ({report.summary.totalSize} bytes)");
        }

        private static string GetOutputPath(string defaultPath)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-outputPath", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return defaultPath;
        }
    }
}