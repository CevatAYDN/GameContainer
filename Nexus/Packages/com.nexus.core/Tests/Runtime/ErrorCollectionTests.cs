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
            bool wasSuspended = ErrorCollection.Suspended; // the editor suspends collection while tests run (issue #193)
            if (enabledHere) ErrorCollection.Enabled = true;
            ErrorCollection.CaptureUnityLogs = true;
            ErrorCollection.Suspended = false;
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
                ErrorCollection.Suspended = wasSuspended;
                ErrorCollection.Clear();
            }
        }

#if UNITY_EDITOR
        // Issue #193: the errors tests provoke on purpose stayed in the Nexus window's Error Dashboard after a run, both
        // the Unity logs ErrorCollection mirrors and the framework's own Collect calls. The editor's TestRunErrorFilter
        // suspends collection for the run, also after the domain reload a PlayMode run makes.
        [Test]
        public void WhileATestRuns_NothingReachesTheErrorDashboard()
        {
            bool enabledHere = !ErrorCollection.Enabled;
            if (enabledHere) ErrorCollection.Enabled = true;
            try
            {
                const string logged = "issue-193 a Unity log written during a test run";
                const string collected = "issue-193 an error collected during a test run";
                Debug.Log(logged);
                ErrorCollection.Collect(ErrorCollection.ErrorSeverity.Error, ErrorCollection.ErrorCategory.General, collected, logToConsole: false);

                foreach (var entry in ErrorCollection.GetErrors(null, null, ErrorCollection.MaxErrors))
                {
                    Assert.AreNotEqual(logged, entry.Message);
                    Assert.AreNotEqual(collected, entry.Message);
                }
            }
            finally
            {
                if (enabledHere) ErrorCollection.Enabled = false;
            }
        }
#endif
    }
}
