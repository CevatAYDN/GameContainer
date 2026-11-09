using System;
using System.Collections;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class FullReviewUnityServiceMetricsTests
    {
        // U05 / #369: the engine's unscaled interval is independent of the reported metric.
        [UnityTest]
        public IEnumerator FrameMetrics_UseRealFrameTimeWhileTheGameIsPaused()
            => ObserveFrameMetrics(0f);

        [UnityTest]
        public IEnumerator FrameMetrics_UseRealFrameTimeWhileTheGameRunsAtHalfSpeed()
            => ObserveFrameMetrics(0.5f);

        private static IEnumerator ObserveFrameMetrics(float scale)
        {
            float previousScale = Time.timeScale;
            bool previouslyEnabled = PerformanceMonitor.Enabled;
            bool previouslyRecording = PerformanceMonitor.IsRecording;
            int changedAtFrame = Time.frameCount;
            int fpsFrame = -1;
            int durationFrame = -2;
            int gameDeltaFrame = -3;
            float fps = 0f;
            float durationMs = 0f;
            float unscaledSeconds = 0f;
            float gameDeltaSeconds = 0f;
            float expectedGameDelta = 0f;
            Action<PerformanceMonitor.MetricSample> observe = sample =>
            {
                if (Time.frameCount <= changedAtFrame + 1 || sample.Category != "Frame") return;
                if (Time.unscaledDeltaTime <= 0f) return;
                if (sample.Name == "FPS")
                {
                    fpsFrame = Time.frameCount;
                    fps = sample.Value;
                    unscaledSeconds = Time.unscaledDeltaTime;
                }
                else if (sample.Name == "FrameTime")
                {
                    durationFrame = Time.frameCount;
                    durationMs = sample.Value;
                }
                else if (sample.Name == "DeltaTime")
                {
                    gameDeltaFrame = Time.frameCount;
                    gameDeltaSeconds = sample.Value;
                    expectedGameDelta = Time.deltaTime;
                }
            };

            try
            {
                PerformanceMonitor.OnMetricRecorded += observe;
                if (!previouslyEnabled) PerformanceMonitor.Enabled = true;
                if (!previouslyRecording) PerformanceMonitor.StartRecording();
                Time.timeScale = scale;
                double deadline = Time.realtimeSinceStartupAsDouble + 5d;
                while ((fpsFrame != durationFrame || fpsFrame != gameDeltaFrame) && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    yield return null;
                    PerformanceMonitor.UpdateFrameMetrics();
                }

                Assert.AreEqual(scale, Time.timeScale, "U05 setup: the requested game speed must remain in effect.");
                Assert.IsTrue(fpsFrame > changedAtFrame + 1 && fpsFrame == durationFrame && fpsFrame == gameDeltaFrame,
                    "U05 setup: the metrics must be recorded for the same real frame; no throttle reset is used.");
                float expectedFps = 1f / unscaledSeconds;
                float expectedMs = unscaledSeconds * 1000f;
                Assert.That(fps, Is.EqualTo(expectedFps).Within(Mathf.Max(0.01f, expectedFps * 0.001f)),
                    $"U05: timeScale={scale} must not scale reported FPS (unscaled interval {unscaledSeconds}s).");
                Assert.That(durationMs, Is.EqualTo(expectedMs).Within(Mathf.Max(0.001f, expectedMs * 0.001f)),
                    $"U05: timeScale={scale} must not scale reported frame milliseconds.");
                Assert.That(gameDeltaSeconds, Is.EqualTo(expectedGameDelta).Within(0.000001f),
                    "U05: the existing DeltaTime metric still describes scaled game time.");
            }
            finally
            {
                PerformanceMonitor.OnMetricRecorded -= observe;
                Time.timeScale = previousScale;
                if (!previouslyRecording) PerformanceMonitor.StopRecording();
                if (PerformanceMonitor.Enabled != previouslyEnabled) PerformanceMonitor.Enabled = previouslyEnabled;
            }
        }
    }
}
