using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Nexus.Editor.Ios
{
    /// <summary>
    /// Builds an Xcode project for the iOS Simulator (store screenshots; #436) and puts the settings back afterwards. Unity 6000.6 ships
    /// only a development Simulator runtime, so the build is always Development. Not compiled or run when it was written. The Simulator
    /// architecture is not set here (no API was verifiable without a compile): Apple silicon editors build arm64 by default.
    /// </summary>
    public static class IosSimulatorBuild
    {
        /// <summary>Builds the enabled scenes into <paramref name="outputPath"/>; returns the report summary.</summary>
        public static BuildSummary Build(string outputPath)
        {
            var sdk = PlayerSettings.iOS.sdkVersion;
            bool development = EditorUserBuildSettings.development;
            try
            {
                PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
                EditorUserBuildSettings.development = true;
                var scenes = new System.Collections.Generic.List<string>();
                foreach (var scene in EditorBuildSettings.scenes)
                {
                    if (scene.enabled) scenes.Add(scene.path);
                }
                var options = new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = outputPath,
                    target = BuildTarget.iOS,
                    options = BuildOptions.Development
                };
                return BuildPipeline.BuildPlayer(options).summary;
            }
            finally
            {
                PlayerSettings.iOS.sdkVersion = sdk;
                EditorUserBuildSettings.development = development;
            }
        }

        /// <summary>Batch entry: <c>-executeMethod Nexus.Editor.Ios.IosSimulatorBuild.BuildCli -iosSimulatorOutput &lt;folder&gt;</c>; exit 0 built, 1 failed, 2 no output path.</summary>
        public static void BuildCli()
        {
            string output = IosShipCheckCli.Argument("-iosSimulatorOutput");
            if (string.IsNullOrEmpty(output)) { Debug.LogError("[Nexus iOS] -iosSimulatorOutput <folder> is required."); EditorApplication.Exit(2); return; }
            var summary = Build(output);
            Debug.Log("[Nexus iOS] Simulator build " + summary.result + " (" + summary.totalErrors + " errors) at " + output);
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
