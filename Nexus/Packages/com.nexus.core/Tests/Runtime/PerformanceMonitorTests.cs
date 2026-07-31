using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests
{
    [TestFixture]
    [Ignore("bisect: temporarily excluded to isolate PlayMode hang poison")]
    public class PerformanceMonitorTests
    {
        [SetUp]
        public void Setup()
        {
            PerformanceMonitor.Enabled = true;
            PerformanceMonitor.MaxHistorySize = 300;
            PerformanceMonitor.StopRecording();
            PerformanceMonitor.ClearHistory();
            // The frame-metric throttle is static; reset it so the first-call test is
            // deterministic regardless of test ordering or frame-count drift.
            PerformanceMonitor.ResetFrameThrottle();
        }

        [TearDown]
        public void TearDown()
        {
            PerformanceMonitor.Enabled = true;
            PerformanceMonitor.MaxHistorySize = 300;
            PerformanceMonitor.StopRecording();
            PerformanceMonitor.ClearHistory();
            PerformanceMonitor.ResetFrameThrottle();
        }

        [Test]
        public void RecordMetric_WhileRecording_EnqueuesSamples()
        {
            PerformanceMonitor.StartRecording();
            PerformanceMonitor.RecordMetric("TestMetric", 42f, "n", "Custom");

            var samples = PerformanceMonitor.GetRecentSamples(100);
            Assert.AreEqual(1, samples.Length);
            Assert.AreEqual("TestMetric", samples[0].Name);
            Assert.AreEqual(42f, samples[0].Value);
        }

        [Test]
        public void RecordMetric_WhileStopped_DoesNotEnqueueSamples_ButTracksCurrentValue()
        {
            PerformanceMonitor.StopRecording();
            PerformanceMonitor.RecordMetric("StoppedMetric", 7f, "n", "Custom");

            Assert.AreEqual(0, PerformanceMonitor.GetRecentSamples(100).Length,
                "No samples may be enqueued while recording is stopped.");
            Assert.AreEqual(7f, PerformanceMonitor.GetMetric("StoppedMetric"),
                "The latest value must remain queryable even when not recording.");
        }

        [Test]
        public void RecordMetric_HighVolume_KeepsSampleQueueBounded()
        {
            // Regression: the samples queue used to grow forever between ClearHistory drains,
            // leaking managed heap (observed climbing to ~800 MB). It must stay capped.
            PerformanceMonitor.StartRecording();
            for (int i = 0; i < 5000; i++)
                PerformanceMonitor.RecordMetric("Burst", i, "n", "Custom");

            var samples = PerformanceMonitor.GetRecentSamples(10000);
            Assert.LessOrEqual(samples.Length, 2000,
                $"Sample queue must be bounded, got {samples.Length}.");
        }

        [Test]
        public void RecordMetric_History_IsBoundedFifo()
        {
            PerformanceMonitor.MaxHistorySize = 10;
            PerformanceMonitor.StartRecording();
            for (int i = 0; i < 100; i++)
                PerformanceMonitor.RecordMetric("Hist", i, "n", "Custom");

            var history = PerformanceMonitor.GetMetricHistory("Hist");
            Assert.AreEqual(10, history.Length, "History must be capped at MaxHistorySize.");
            Assert.AreEqual(90f, history[0], "Oldest entries must be evicted first (FIFO).");
            Assert.AreEqual(99f, history[history.Length - 1], "Newest value must be the last element.");
        }

        [Test]
        public void RecordMetric_History_RemainsUsableWhileStopped()
        {
            PerformanceMonitor.StartRecording();
            PerformanceMonitor.RecordMetric("Persistent", 1f, "n", "Custom");
            PerformanceMonitor.StopRecording();
            PerformanceMonitor.RecordMetric("Persistent", 2f, "n", "Custom");

            var history = PerformanceMonitor.GetMetricHistory("Persistent");
            Assert.AreEqual(2, history.Length,
                "Bounded history should keep updating while stopped (no leak, but no samples).");
            Assert.AreEqual(0, PerformanceMonitor.GetRecentSamples(100).Length);
        }

        [Test]
        public void UpdateFrameMetrics_FirstCall_RecordsImmediately()
        {
            // Regression: the throttle guard used an initial value of -1, so the first ~5
            // frames silently dropped their sample. The first call must always record.
            PerformanceMonitor.StartRecording();
            PerformanceMonitor.UpdateFrameMetrics();

            var samples = PerformanceMonitor.GetRecentSamples(100);
            Assert.GreaterOrEqual(samples.Length, 3, "FPS/FrameTime/DeltaTime should be recorded on the first call.");
        }

        [Test]
        public void ClearHistory_ResetsSamplesAndHistory()
        {
            PerformanceMonitor.StartRecording();
            for (int i = 0; i < 50; i++)
                PerformanceMonitor.RecordMetric("ClearMe", i, "n", "Custom");

            PerformanceMonitor.ClearHistory();

            Assert.AreEqual(0, PerformanceMonitor.GetRecentSamples(100).Length);
            Assert.AreEqual(0, PerformanceMonitor.GetMetricHistory("ClearMe").Length);
            Assert.AreEqual(0f, PerformanceMonitor.GetMetric("ClearMe"));
        }

        [Test]
        public void MaxHistorySize_Setter_ClampsMinimum()
        {
            PerformanceMonitor.MaxHistorySize = 2;
            Assert.AreEqual(10, PerformanceMonitor.MaxHistorySize,
                "MaxHistorySize must never drop below the 10-sample floor.");
        }
    }
}
