#if UNITY_EDITOR
using System;
using System.IO;
using Nexus.Ads.Editor;
using Nexus.Ads.Unity;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsBuildGuardTests
    {
        [Test]
        [Explicit("Requires an isolated batch project, NEXUS_FULL_REVIEW_ADS_BUILD_REPORT pointing to a real Android release .buildreport, and ad-free Resources settings.")]
        public void A05_BuildHooksRejectInvalidSceneSettingsInsteadOfPassingTheResourcesAsset()
        {
            Assert.That(Application.isPlaying, Is.False, "This integration fixture runs in EditMode.");
            if (!Application.isBatchMode)
                Assert.Inconclusive("Run this scene-writing integration fixture in an isolated batch project.");
            string reportPath = Environment.GetEnvironmentVariable("NEXUS_FULL_REVIEW_ADS_BUILD_REPORT");
            if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
                Assert.Inconclusive("Supply a real native Android release BuildReport; this fixture does not fabricate one or build a player.");
            var report = BuildReport.LoadReport(reportPath);
            Assert.That(report, Is.Not.Null, "Unity must load the supplied native report.");
            Assert.That(report.summary.platform, Is.EqualTo(BuildTarget.Android));
            Assert.That((report.summary.options & BuildOptions.Development) == 0, Is.True);

            var canonical = AdsSettings.Load();
            if (canonical == null || canonical.SelectedMediation != AdProviderKind.None || !canonical.AdFreeRelease)
                Assert.Inconclusive("The fixture requires an existing ad-free Resources asset; it never edits or replaces the game's settings.");

            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    Assert.Inconclusive("The integration fixture must not replace or save dirty editor scenes.");
            var originalScenes = EditorBuildSettings.scenes;
            string validationPath = AdsValidator.BuildReportPath;
            byte[] previousReport = File.Exists(validationPath) ? File.ReadAllBytes(validationPath) : null;
            string folder = "Assets/FullReviewAdsBuildGuard_" + Guid.NewGuid().ToString("N");
            Scene scene = default;
            AdsSettings sceneSettings = null;
            try
            {
                new AdsBuildGuard().OnPreprocessBuild(report);
                Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                // NewScene unloads assets kept only on this method's managed stack.
                canonical = AdsSettings.Load();
                report = BuildReport.LoadReport(reportPath);
                Assert.That(canonical != null && report != null, Is.True, "Reload the native inputs after replacing the batch scene.");
                sceneSettings = ScriptableObject.CreateInstance<AdsSettings>();
                sceneSettings.SelectedMediation = AdProviderKind.None;
                sceneSettings.AdFreeRelease = false;
                AssetDatabase.CreateAsset(sceneSettings, folder + "/SceneAdsSettings.asset");
                var root = new GameObject("FullReviewAdsSceneOwner");
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                var bootstrap = root.AddComponent<AdsBootstrap>();
                bootstrap.Configure(sceneSettings, startOnAwake: false);
                root.SetActive(true);
                string scenePath = folder + "/Scene.unity";
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                // Custom BuildPlayerOptions can include a scene absent from EditorBuildSettings.
                EditorBuildSettings.scenes = Array.Empty<EditorBuildSettingsScene>();
                Assert.That(bootstrap.Settings, Is.SameAs(sceneSettings));
                Assert.That(AdsSettings.Load(), Is.SameAs(canonical), "The mismatched Resources asset must remain unchanged.");

                Assert.Throws<BuildFailedException>(() => RunBuildHooks(report, scene),
                    "The processed build scene uses settings that forbid this release; the ad-free Resources asset cannot authorize it.");
                Assert.DoesNotThrow(() => new AdsBuildGuard().OnProcessScene(scene, null),
                    "Scene processing outside a player build must not enforce release settings.");
                sceneSettings.AdFreeRelease = true;
                Assert.DoesNotThrow(() => RunBuildHooks(report, scene),
                    "The same scene with valid release settings must pass.");
            }
            finally
            {
                EditorBuildSettings.scenes = originalScenes;
                if (originalSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (sceneSettings != null) UnityEngine.Object.DestroyImmediate(sceneSettings);
                if (previousReport == null)
                {
                    if (File.Exists(validationPath)) File.Delete(validationPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(validationPath));
                    File.WriteAllBytes(validationPath, previousReport);
                }
            }
        }

        private static void RunBuildHooks(BuildReport report, Scene scene)
        {
            var guard = new AdsBuildGuard();
            guard.OnPreprocessBuild(report);
            if ((object)guard is IProcessSceneWithReport sceneGuard)
                sceneGuard.OnProcessScene(scene, report);
        }
    }
}
#endif
