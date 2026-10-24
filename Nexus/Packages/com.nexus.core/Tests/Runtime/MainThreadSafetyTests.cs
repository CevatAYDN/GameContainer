using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    public class MainThreadSafetyTests
    {
        // Issue #34: SafeDestroy called Object.Destroy on whatever thread it ran on; from a worker
        // (a dispose off the main thread) Unity threw UnityException.
        [UnityTest]
        public IEnumerator SafeDestroy_FromAWorkerThread_DestroysOnTheMainThread()
        {
            var target = new GameObject("issue-34");
            Exception thrown = null;
            var worker = Task.Run(() =>
            {
                try { SafeDestroyUtility.SafeDestroy(target); }
                catch (Exception ex) { thrown = ex; }
            });
            while (!worker.IsCompleted) yield return null;
            // the posted destroy runs on the main thread; Destroy completes at the end of that frame
            yield return null;
            yield return null;

            Assert.IsNull(thrown, thrown?.ToString());
            Assert.IsTrue(target == null, "the object was destroyed");
        }
    }
}
