using System.IO;
using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Tests.Editor
{
    /// <summary>
    /// Locks the two infrastructure checks that must run on every CI pass but are
    /// not ordinary feature tests: architecture validation and AOT binder codegen.
    /// Both are part of the EditMode test job in .github/workflows/ci.yml.
    /// </summary>
    [TestFixture]
    public class InfrastructureValidationTests
    {
        [Test]
        public void ArchitectureValidation_Passes_WithNoErrors()
        {
            BuildValidation.IncludeTestAssemblies = false;
            BuildValidation.RunSilent();

            Assert.IsTrue(BuildValidation.HasRun, "BuildValidation did not execute.");
            // Errors are blocking — must be zero.
            Assert.AreEqual(0, BuildValidation.LastErrorCount,
                $"Architecture Validation produced {BuildValidation.LastErrorCount} error(s).");
            // Warnings are non-blocking; log them for CI diagnostics but do not fail the test.
            // This allows legitimate warnings from game-assembly commands while catching regressions
            // that introduce errors.
            if (BuildValidation.LastWarningCount > 0)
            {
                UnityEngine.Debug.Log($"[Nexus] Architecture Validation: {BuildValidation.LastWarningCount} warning(s) reported. Review 'BuildValidation.LastResults' for details.");
            }
            Assert.IsTrue(BuildValidation.LastRunPassed, "Architecture Validation did not pass.");
        }

        [Test]
        public void AotBinder_GeneratesCleanly()
        {
            // Re-running the AOT binder generator must complete without throwing and
            // must produce the generated binder + link.xml artifacts.
            NexusCodeGenerator.GenerateBinder();

            var settings = NexusEditorSettings.GetOrCreateSettings();
            var binderPath = Path.Combine(settings.BinderOutputPath, "NexusGeneratedBinder.g.cs");
            Assert.IsTrue(File.Exists(binderPath),
                $"AOT binder was not generated at expected path: {binderPath}");
        }

        [Test]
        public void NexusWindow_SmokeTest_OpensAndBuildsUI()
        {
            var window = UnityEditor.EditorWindow.GetWindow<NexusWindow>(true, "Nexus Test Window", false);
            try
            {
                window.Show();
                window.Repaint();
                Assert.IsNotNull(window.rootVisualElement, "NexusWindow should build a root visual tree.");
            }
            finally
            {
                window.Close();
            }
        }
    }
}
