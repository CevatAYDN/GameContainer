using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Extensions;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// <see cref="SceneLoader"/> against Unity's own SceneManager. The duplicate, cancelled and subscriber-failed loads are checked
    /// in the host harness (ServicesSuite S1 to S7), whose SceneManager stand-in can hold an operation open.
    /// </summary>
    public sealed class SceneLoaderTests
    {
        // Issue #188: no test asserted SceneLoadFailedSignal, the signal a loading screen waits on when a load does not complete
        [Test]
        public void ASceneMissingFromTheBuild_FiresSceneLoadFailed_AndTheCallReturns()
        {
            using var test = NexusTestHarness.CreateContext();
            string failed = null;
            test.Context.SignalBus.Subscribe<SceneLoadFailedSignal>(s => failed = s.SceneName + ": " + s.Error);
            LogAssert.Expect(LogType.Error, new Regex("Scene 'NoSuchScene188' couldn't be loaded"));
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] Scene 'NoSuchScene188' not found in build settings\."));

            var load = new SceneLoader(test.Context.SignalBus).LoadSceneAsync("NoSuchScene188");

            Assert.IsTrue(load.IsCompleted, "nothing to wait for");
            Assert.AreEqual("NoSuchScene188: Scene not found in build settings.", failed);
        }
    }
}
