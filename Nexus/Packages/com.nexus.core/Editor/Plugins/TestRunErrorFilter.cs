#if NEXUS_TEST_FRAMEWORK
using Nexus.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace Nexus.Editor
{
    /// <summary>
    /// Keeps a test run out of the Error Dashboard (issue #193). Tests provoke errors on purpose, and the dashboard lists
    /// the game's own problems. <see cref="ErrorCollection.Suspended"/> is set from the run's start to its end, also
    /// across the domain reload a PlayMode run makes, which resets it.
    /// </summary>
    internal sealed class TestRunErrorFilter : IErrorCallbacks
    {
        private const string RunningKey = "Nexus.TestRun.Running";

        [InitializeOnLoadMethod]
        private static void Register()
        {
            TestRunnerApi.RegisterTestCallback(new TestRunErrorFilter());
            if (SessionState.GetBool(RunningKey, false)) ErrorCollection.Suspended = true;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            SessionState.SetBool(RunningKey, true);
            ErrorCollection.Suspended = true;
        }

        public void RunFinished(ITestResultAdaptor result) => End();

        public void OnError(string message) => End();

        public void TestStarted(ITestAdaptor test) { }

        public void TestFinished(ITestResultAdaptor result) { }

        private static void End()
        {
            SessionState.EraseBool(RunningKey);
            ErrorCollection.Suspended = false;
        }
    }
}
#endif
