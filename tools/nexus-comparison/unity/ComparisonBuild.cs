using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ComparisonBuild
{
    /// <summary>
    /// Reflex reads a ReflexSettings ScriptableObject from Resources in its logger's static constructor and asserts it exists;
    /// in a non-development player the assertion is compiled out and the missing asset becomes a null dereference on
    /// Container.Dispose. The asset is created here with LogLevel = Warning so disposal does not log (the host run uses a
    /// silent logger shim), keeping the register/build/first-resolve workload comparable. The type is internal to the Reflex
    /// assembly, so it is created by class name and its serialized backing field is set through SerializedObject.
    /// </summary>
    private static void CreateReflexSettings()
    {
        const string path = "Assets/Resources/ReflexSettings.asset";
        if (File.Exists(path)) return;
        Directory.CreateDirectory("Assets/Resources");
        var settings = ScriptableObject.CreateInstance("ReflexSettings");
        if (settings == null) throw new InvalidOperationException("Reflex is not compiled into the comparison project (ReflexSettings type not found).");
        var serialized = new SerializedObject(settings);
        var logLevel = serialized.FindProperty("<LogLevel>k__BackingField");
        if (logLevel == null) throw new InvalidOperationException("ReflexSettings has no serialized LogLevel; the pinned Reflex revision changed.");
        logLevel.intValue = 2; // Reflex.Logging.LogLevel.Warning
        serialized.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.CreateAsset(settings, path);
        AssetDatabase.SaveAssets();
    }

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
            CreateReflexSettings();
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
