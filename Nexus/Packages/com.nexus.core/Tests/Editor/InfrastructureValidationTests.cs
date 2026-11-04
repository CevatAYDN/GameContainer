using System.IO;
using System.Linq;
using NUnit.Framework;
using Nexus.Editor;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Locks architecture validation, an infrastructure check that is not an ordinary
    /// feature test, with the package's EditMode tests. BuildWiringTests covers the
    /// AOT binder codegen.
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
                $"Architecture Validation produced {BuildValidation.LastErrorCount} error(s).\n" +
                string.Join("\n", BuildValidation.LastResults.Where(entry => entry.IsError).Select(entry => entry.Message)));
            // Warnings are non-blocking; log them for CI diagnostics but do not fail the test.
            // This allows legitimate warnings from game-assembly commands while catching regressions
            // that introduce errors.
            if (BuildValidation.LastWarningCount > 0)
            {
                UnityEngine.Debug.Log($"[Nexus] Architecture Validation: {BuildValidation.LastWarningCount} warning(s) reported. Review 'BuildValidation.LastResults' for details.");
            }
            Assert.IsTrue(BuildValidation.LastRunPassed, "Architecture Validation did not pass.");
        }
    }
}
