using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Nexus.Core;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #193: the Context Wizard's generators overwrote existing scripts with File.WriteAllText, and accepted any
    /// non-empty name, so "1Player" or "class" wrote code that does not compile.
    /// </summary>
    [TestFixture]
    public class ScriptGeneratorTests
    {
        private string _folder;

        [SetUp]
        public void SetUp() => _folder = Path.Combine(Path.GetTempPath(), "NexusScriptGenerator_" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Test]
        public void AGenerator_WritesNothing_WhenOneOfItsFilesExists()
        {
            var files = ScriptGenerator.Service(_folder, "PlayerData");
            string mine = files[1].Path;
            Directory.CreateDirectory(Path.GetDirectoryName(mine));
            File.WriteAllText(mine, "// the user's own service");

            Assert.IsFalse(ScriptGenerator.TryWrite(files, out var existing));

            CollectionAssert.AreEqual(new[] { mine }, existing);
            Assert.AreEqual("// the user's own service", File.ReadAllText(mine), "the existing file was overwritten");
            Assert.IsFalse(File.Exists(files[0].Path), "the other file was written although the set was refused");
        }

        [Test]
        public void TheGenerators_RefuseANameThatIsNotACSharpTypeName()
        {
            foreach (var name in new[] { null, "", "1Player", "Player Data", "class" })
                Assert.IsNotNull(ScriptGenerator.TypeNameError(name), $"'{name}' was accepted");
            Assert.IsNull(ScriptGenerator.TypeNameError("PlayerData"));
        }

        [Test]
        public void EveryGeneratedScript_CompilesWithUnityRoslyn()
        {
            var files = ScriptGenerator.Service(_folder, "PlayerData")
                .Concat(ScriptGenerator.ViewMediator(_folder, "GameHUD"))
                .Concat(ScriptGenerator.SignalCommand(_folder, "ScoreChanged", "UpdateScoreCommand"))
                .ToArray();
            Assert.IsTrue(ScriptGenerator.TryWrite(files, out _));

            WizardTemplateSyncTests.AssertCompiles(_folder);
        }

        [Test]
        public void TheContextGenerator_RefusesAnExistingContextDataAsset_AndScansTheAssembliesItWasGiven()
        {
            string folder = "Assets/NexusContextGeneratorTest_" + Guid.NewGuid().ToString("N");
            Root first = null;
            try
            {
                first = ContextGenerator.Create("WizardTest", folder, null, new[] { "Assembly-CSharp" }, out var error);
                Assert.IsNotNull(first, error);
                Assert.That(first.ContextData.AssemblyScopes, Is.EqualTo(new[] { "Assembly-CSharp" }));
                Assert.AreEqual("WizardTest", first.ContextData.ScopeTag);

                var second = ContextGenerator.Create("WizardTest", folder, null, Array.Empty<string>(), out error);

                Assert.IsNull(second, "a second Root was created over the existing ContextData");
                StringAssert.Contains(folder + "/WizardTestContextData.asset", error);
                var kept = AssetDatabase.LoadAssetAtPath<ContextData>(folder + "/WizardTestContextData.asset");
                Assert.That(kept.AssemblyScopes, Is.EqualTo(new[] { "Assembly-CSharp" }), "the existing asset was replaced");
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first.gameObject);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
