"""Create a disposable Unity project that installs the packed com.nexus.core tarball.

Usage: python tools/nexus-consumer/prepare_clean_project.py --archive <tgz> --output <folder>

The project is named CleanConsumer under <folder>; the editor methods in it refuse to run
anywhere else. The development project under Nexus/ is never touched. The smoke assets import
the Code-first Startup sample, build a scene around it, run EditMode/PlayMode tests and can
build a Windows Mono player and an Android IL2CPP player that report their own result.
"""
import argparse
import hashlib
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEV_PROJECT = REPO / "Nexus"

parser = argparse.ArgumentParser()
parser.add_argument("--archive", type=Path, required=True, help="com.nexus.core-<version>.tgz that run.ps1 packs with git archive")
parser.add_argument("--output", type=Path, required=True, help="evidence folder; the project is created at <output>/CleanConsumer")
parser.add_argument("--testables", action="store_true", help="list com.nexus.core in testables, so the package's own tests compile in the consumer")
args = parser.parse_args()
archive = args.archive.resolve()
if not archive.is_file():
    raise SystemExit(f"Archive not found: {archive}")
project = args.output.resolve() / "CleanConsumer"
if project.exists():
    raise SystemExit(f"Refusing to overwrite an existing consumer: {project}")


def write(relative, content):
    target = project / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content.strip() + "\n", encoding="utf-8")


original = json.loads((DEV_PROJECT / "Packages/manifest.json").read_text(encoding="utf-8-sig"))
dependencies = {key: value for key, value in original["dependencies"].items() if key.startswith("com.unity.modules.")}
dependencies.update({
    "com.nexus.core": "file:" + archive.as_posix(),
    "com.unity.test-framework": original["dependencies"].get("com.unity.test-framework", "1.8.0"),
    # The Unity CLI (`unity run` / `unity test`) drives the project through this package; the development project carries the same version.
    "com.unity.pipeline": original["dependencies"].get("com.unity.pipeline", "0.9.1-exp.1"),
})
# A game lists no testables; only the package's own suites need them (issue #74).
manifest = {"dependencies": dependencies, **({"testables": ["com.nexus.core"]} if args.testables else {})}
write("Packages/manifest.json", json.dumps(manifest, indent=2))
write("ProjectSettings/ProjectVersion.txt", (DEV_PROJECT / "ProjectSettings/ProjectVersion.txt").read_text())

write("Assets/Smoke/Runtime/NexusIntegrationSmoke.Runtime.asmdef", json.dumps({
    "name": "NexusIntegrationSmoke.Runtime", "references": ["com.nexus.core"], "autoReferenced": True,
}, indent=2))
write("Assets/Smoke/Runtime/SmokeAccess.cs", r'''
using System;
using System.Reflection;
using Nexus.Core;
using UnityEngine;

namespace NexusIntegrationSmoke
{
    public static class SmokeAccess
    {
        public static Type Find(string name)
        {
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            throw new InvalidOperationException("Imported sample type missing: " + name);
        }
        public static Context ContextOf(MonoBehaviour owner) => (Context)owner.GetType()
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        public static int ScoreOf(Context context)
        {
            var model = context.Container.Resolve(Find("Nexus.Samples.CodeFirst.ScoreModel"));
            var score = model.GetType().GetProperty("Score").GetValue(model);
            return (int)score.GetType().GetProperty("Value").GetValue(score);
        }
    }
}
''')
write("Assets/Smoke/Runtime/PlayerSmokeProbe.cs", r'''
using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace NexusIntegrationSmoke
{
    /// <summary>Waits for the code-first owner to start, checks the score, destroys the owner and checks disposal; writes --nexus-result.</summary>
    public sealed class PlayerSmokeProbe : MonoBehaviour
    {
        public MonoBehaviour Owner;
        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + 30f;
            while (Owner != null && SmokeAccess.ContextOf(Owner) == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            bool passed = false;
            try
            {
                var context = SmokeAccess.ContextOf(Owner);
                if (context == null || SmokeAccess.ScoreOf(context) != 1)
                    throw new InvalidOperationException("Code-first startup did not reach score 1.");
                var modelType = SmokeAccess.Find("Nexus.Samples.CodeFirst.ScoreModel");
                Destroy(Owner.gameObject);
                Owner = null;
                yield return null; // let Unity deliver OnDestroy
                try { context.Container.Resolve(modelType); }
                catch (ObjectDisposedException) { passed = true; }
                if (!passed) throw new InvalidOperationException("Owner destruction did not dispose its container.");
            }
            finally
            {
                var args = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(args, "--nexus-result");
                if (index >= 0 && index + 1 < args.Length)
                    File.WriteAllText(args[index + 1], passed ? "{\"passed\":true}" : "{\"passed\":false}");
                Debug.Log(passed ? "NEXUS_PLAYER_SMOKE_PASS" : "NEXUS_PLAYER_SMOKE_FAIL");
                Application.Quit(passed ? 0 : 2);
            }
        }
    }
}
''')
write("Assets/Smoke/Editor/CleanConsumerSetup.cs", r'''
using System;
using System.IO;
using System.Linq;
using Nexus.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;
using NexusIntegrationSmoke;

public static class CleanConsumerSetup
{
    private static void GuardConsumer()
    {
        if (new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name != "CleanConsumer")
            throw new InvalidOperationException("This runner only operates on the isolated CleanConsumer.");
    }
    public static void ImportSample()
    {
        try
        {
            GuardConsumer();
            var package = PackageInfo.FindForAssetPath("Packages/com.nexus.core/package.json");
            if (package == null || package.source.ToString() != "LocalTarball")
                throw new InvalidOperationException("Expected the packaged local tarball, not an embedded/development dependency.");
            var sample = Sample.FindByPackage(package.name, package.version).Single(item => item.displayName == "Code-first Startup");
            if (!sample.Import(Sample.ImportOptions.OverridePreviousImports))
                throw new InvalidOperationException("UPM Sample.Import returned false.");
            Debug.Log("NEXUS_UPM_SAMPLE_IMPORTED: " + sample.importPath);
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
    }
    public static void PrepareScene()
    {
        try
        {
            GuardConsumer();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var owner = new GameObject("Nexus Code-first Owner").AddComponent(SmokeAccess.Find("Nexus.Samples.CodeFirst.ScoreBootstrap")) as MonoBehaviour;
            var probe = new GameObject("Player Smoke Probe").AddComponent<PlayerSmokeProbe>();
            probe.Owner = owner;
            Directory.CreateDirectory("Assets/Scenes");
            if (!EditorSceneManager.SaveScene(scene, "Assets/Scenes/CodeFirstSmoke.unity"))
                throw new InvalidOperationException("Could not save the smoke scene.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/CodeFirstSmoke.unity", true) };
            BuildValidation.ValidateOrThrow();
            AssetDatabase.SaveAssets();
            Debug.Log("NEXUS_ARCHITECTURE_VALIDATION_PASS");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
    }
    public static void BuildWindowsMono()
    {
        try
        {
            GuardConsumer();
            BuildValidation.ValidateOrThrow();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            Directory.CreateDirectory("../player-mono");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/Scenes/CodeFirstSmoke.unity" },
                locationPathName = "../player-mono/NexusSmoke.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            });
            Debug.Log("NEXUS_BUILD_RESULT: " + report.summary.result);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 2);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
    }
    public static void BuildAndroidIl2Cpp()
    {
        try
        {
            GuardConsumer();
            BuildValidation.ValidateOrThrow();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.nexus.validation.smoke");
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory("../player-android-il2cpp");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/Scenes/CodeFirstSmoke.unity" },
                locationPathName = "../player-android-il2cpp/NexusSmoke.apk",
                target = BuildTarget.Android,
                options = BuildOptions.Development,
            });
            Debug.Log("NEXUS_ANDROID_IL2CPP_BUILD_RESULT: " + report.summary.result);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 2);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(2); }
    }
}
''')
write("Assets/Smoke/Tests/Editor/NexusIntegrationSmoke.Editor.Tests.asmdef", json.dumps({
    "name": "NexusIntegrationSmoke.Editor.Tests", "includePlatforms": ["Editor"],
    "references": ["com.nexus.core", "com.nexus.core.editor", "com.nexus.core.testing"],
    "optionalUnityReferences": ["TestAssemblies"], "autoReferenced": False,
}, indent=2))
write("Assets/Smoke/Tests/Editor/UpmConsumerTests.cs", r'''
using System.IO;
using System.Linq;
using NUnit.Framework;
using Nexus.Editor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.UI;

public sealed class UpmConsumerTests
{
    [Test] public void InstalledArchive_ContainsExactlyTheImportedSample()
    {
        var package = PackageInfo.FindForAssetPath("Packages/com.nexus.core/package.json");
        Assert.IsNotNull(package);
        Assert.AreEqual("LocalTarball", package.source.ToString());
        var sample = Sample.FindByPackage(package.name, package.version).Single(item => item.displayName == "Code-first Startup");
        Assert.IsTrue(sample.isImported);
        Assert.AreEqual(File.ReadAllText(Path.Combine(package.resolvedPath, "Samples~/CodeFirst/ScoreBootstrap.cs")),
            File.ReadAllText(Path.Combine(sample.importPath, "ScoreBootstrap.cs")));
    }
    [Test] public void ProductionArchitectureValidation_Passes()
    {
        BuildValidation.IncludeTestAssemblies = false;
        Assert.IsTrue(BuildValidation.Validate());
        Assert.AreEqual(0, BuildValidation.LastErrorCount);
    }
    private struct KitSignal { }
    // The game's own test assembly references com.nexus.core.testing; the manifest lists no testables (issue #74).
    [Test] public void TestingKit_ReachesAGameWithoutTestables()
    {
        using var test = Nexus.Core.NexusTestHarness.CreateContext();
        Assert.IsNotNull(test.Context);
        var bus = new Nexus.Core.RecordingSignalBus();
        bus.Fire(new KitSignal());
        Assert.AreEqual(1, bus.Count<KitSignal>());
        var prefs = new Nexus.Core.InMemoryPlayerPrefsService();
        prefs.SetBool("kit", true);
        Assert.IsTrue(prefs.GetBool("kit"));
        Assert.AreEqual(Nexus.Core.Services.AdOutcome.Closed, new Nexus.Core.Services.MockAdNetworkAdapter { RewardOutcome = Nexus.Core.Services.AdOutcome.Closed }.RewardOutcome);
        using (new Nexus.Core.GcAllocationProbe()) { }
    }
}
''')
write("Assets/Smoke/Tests/PlayMode/NexusIntegrationSmoke.PlayMode.Tests.asmdef", json.dumps({
    "name": "NexusIntegrationSmoke.PlayMode.Tests",
    "references": ["com.nexus.core", "NexusIntegrationSmoke.Runtime"],
    "optionalUnityReferences": ["TestAssemblies"], "autoReferenced": False,
}, indent=2))
write("Assets/Smoke/Tests/PlayMode/CodeFirstConsumerTests.cs", r'''
using System;
using System.Collections;
using NexusIntegrationSmoke;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class CodeFirstConsumerTests
{
    [UnityTest] public IEnumerator OneComponent_StartsAtScoreOne_AndDisposesOnDestroy()
    {
        var owner = new GameObject("Code-first Consumer Test");
        try
        {
            LogAssert.Expect(LogType.Log, "Nexus score: 1");
            var bootstrap = owner.AddComponent(SmokeAccess.Find("Nexus.Samples.CodeFirst.ScoreBootstrap")) as MonoBehaviour;
            var deadline = Time.realtimeSinceStartup + 10f;
            while (SmokeAccess.ContextOf(bootstrap) == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            var context = SmokeAccess.ContextOf(bootstrap);
            Assert.IsNotNull(context, "Startup timed out.");
            Assert.AreEqual(1, SmokeAccess.ScoreOf(context));
            var modelType = SmokeAccess.Find("Nexus.Samples.CodeFirst.ScoreModel");
            UnityEngine.Object.Destroy(owner);
            yield return null;
            Assert.Throws<ObjectDisposedException>(() => context.Container.Resolve(modelType));
        }
        finally { if (owner != null) UnityEngine.Object.DestroyImmediate(owner); }
    }
}
''')
identity = {"project": str(project), "archive": str(archive), "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()}
(args.output.resolve() / "consumer-preparation.json").write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
print(json.dumps(identity, indent=2))
