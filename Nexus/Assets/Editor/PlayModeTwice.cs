using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace NexusVerify
{
    /// <summary>
    /// Runs the PlayMode suite twice in one editor, the second run entering play mode in the domain the first one left. With
    /// domain reload off (Unity 6.6's default for new projects, the only mode from 6.8) statics are not reset between play
    /// sessions, which one -runTests run cannot show: it enters play mode once (issue #194). Invoked without -quit:
    /// <c>-executeMethod NexusVerify.PlayModeTwice.Run -twiceResults artifacts/name</c> writes <c>name-run1.xml</c> and
    /// <c>name-run2.xml</c> (NUnit XML, relative to the project) and exits 0 when neither run failed, 1 when one did, 2 when it
    /// could not run (domain reload on).
    /// </summary>
    public static class PlayModeTwice
    {
        private static string s_Prefix;
        private static int s_Run;
        private static bool s_AnyFailed;
        private static TestRunnerApi s_Api;

        public static void Run()
        {
            if (!EditorSettings.enterPlayModeOptionsEnabled || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0)
            {
                Debug.LogError("[PlayModeTwice] domain reload is on; set Enter Play Mode Options to not reload the domain");
                EditorApplication.Exit(2);
                return;
            }
            s_Prefix = Argument("-twiceResults") ?? "artifacts/playmode-twice";
            s_Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_Api.RegisterCallbacks(new Callbacks());
            Start();
        }

        private static void Start()
        {
            s_Run++;
            Debug.Log($"[PlayModeTwice] run {s_Run} starts");
            s_Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.PlayMode }));
        }

        // The next run, or the exit, waits for the test runner's job to end: RunFinished arrives before the runner has left play mode,
        // restored the project settings it changed (runInBackground) and deleted its InitTestScene, and an exit at that point left both.
        private static void ContinueWhenEdit()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || RunActive()) return;
            EditorApplication.update -= ContinueWhenEdit;
            if (s_Run == 1) Start();
            else EditorApplication.Exit(s_AnyFailed ? 1 : 0);
        }

        // shortcut: TestRunnerApi.IsRunActive is internal in com.unity.test-framework 1.8; without it only the play mode exit is awaited.
        private static bool RunActive()
        {
            var method = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.NonPublic | BindingFlags.Static);
            return method != null && (bool)method.Invoke(null, null);
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                string path = $"{s_Prefix}-run{s_Run}.xml";
                TestRunnerApi.SaveResultToFile(result, path);
                s_AnyFailed |= result.FailCount > 0;
                Debug.Log($"[PlayModeTwice] run {s_Run}: passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount} inconclusive={result.InconclusiveCount} -> {path}");
                EditorApplication.update += ContinueWhenEdit;
            }
        }
    }
}
