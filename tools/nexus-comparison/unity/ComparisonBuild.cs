using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ComparisonBuild
{
    public static void BuildMono()
    {
        try
        {
            if (!File.Exists(Path.Combine(Application.dataPath, "NexusComparison.marker")))
                throw new InvalidOperationException("Use the isolated comparison project.");
            string target = Environment.GetEnvironmentVariable("NEXUS_COMPARE_PLAYER");
            if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("NEXUS_COMPARE_PLAYER is required.");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, "");
            PlayerSettings.runInBackground = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Assets/Scenes");
            if (!EditorSceneManager.SaveScene(scene, "Assets/Scenes/Comparison.unity"))
                throw new InvalidOperationException("Could not save comparison scene.");
            var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Comparison.unity" },
                locationPathName = target,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log("NEXUS_COMPARE_BUILD: " + build.summary.result);
            EditorApplication.Exit(build.summary.result == BuildResult.Succeeded ? 0 : 2);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(2);
        }
    }
}
