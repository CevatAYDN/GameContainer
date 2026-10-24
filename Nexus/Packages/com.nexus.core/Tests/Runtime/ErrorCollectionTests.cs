using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    public class ErrorCollectionTests
    {
        // Issue #23 (M7): with Unity log capture on, Collect's own console line came back through
        // logMessageReceivedThreaded and was collected a second time as an ErrorCategory.Unity entry.
        [Test]
        public void ErrorCollection_CollectedErrorsConsoleLine_IsNotCollectedAgain()
        {
            bool enabledHere = !ErrorCollection.Enabled; // the setter also marks Enabled as explicitly set: touch it only when needed
            bool wasCapturing = ErrorCollection.CaptureUnityLogs;
            if (enabledHere) ErrorCollection.Enabled = true;
            ErrorCollection.CaptureUnityLogs = true;
            ErrorCollection.Clear();
            try
            {
                LogAssert.Expect(LogType.Error, "[Nexus Error] issue-23 collected once");
                ErrorCollection.Collect(ErrorCollection.ErrorSeverity.Error, ErrorCollection.ErrorCategory.General, "issue-23 collected once");

                Assert.AreEqual(1, ErrorCollection.TotalErrorCount);
            }
            finally
            {
                if (enabledHere) ErrorCollection.Enabled = false;
                ErrorCollection.CaptureUnityLogs = wasCapturing;
                ErrorCollection.Clear();
            }
        }
    }
}
