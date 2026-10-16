using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Nexus.Ads.AdMob.Tests
{
    /// <summary>
    /// The UMP plugin calls back on the Android UI thread; the adapter's chain stalled there without a
    /// warning. A callback posted from a worker thread must run on the captured (main) thread, and a
    /// missing context must be reported instead of queuing an action that never runs.
    /// </summary>
    public sealed class AdMobMainThreadTests
    {
        [UnityTest]
        public IEnumerator TryPost_FromAWorkerThread_RunsOnTheCapturedThread()
        {
            var main = AdMobMainThread.Capture();
            Assert.IsTrue(main.IsCurrent, "captured on the calling thread");
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            int ranOn = 0;
            bool? posted = null, workerWasCurrent = null;
            var worker = new Thread(() =>
            {
                workerWasCurrent = main.IsCurrent;
                posted = main.TryPost(() => ranOn = Thread.CurrentThread.ManagedThreadId);
            });
            worker.Start();
            worker.Join();
            Assert.AreEqual(false, workerWasCurrent, "a worker thread is not the captured thread");
            Assert.AreEqual(true, posted, "the callback was queued on the captured context");

            var deadline = System.DateTime.UtcNow.AddSeconds(10);
            while (ranOn == 0 && System.DateTime.UtcNow < deadline) yield return null;
            Assert.AreEqual(mainThreadId, ranOn, "the callback ran on the captured thread");
        }

        [Test]
        public void TryPost_WithoutACapturedContext_ReportsFalseAndRunsNothing()
        {
            var none = new AdMobMainThread(null, Thread.CurrentThread.ManagedThreadId);
            bool ran = false;
            Assert.IsFalse(none.TryPost(() => ran = true));
            Assert.IsFalse(ran, "nothing is queued where nobody would execute it");
        }
    }
}
