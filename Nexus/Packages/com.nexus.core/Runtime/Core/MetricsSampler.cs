using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Samples performance, memory, and GC metrics once per frame.
    /// Extracted from Root so each MonoBehaviour owns one concern.
    /// </summary>
    [DefaultExecutionOrder(-800)] // After QueueDrainer, before most scripts
    [Preserve]
    public class MetricsSampler : MonoBehaviour
    {
        // Frame gates shared by all MetricsSampler instances (one per Root) so metrics are
        // sampled at most once per frame even with multiple Roots. Accessed exclusively from
        // Unity's main thread (Update/LateUpdate), so no volatile/locking is needed — the
        // instances never touch these fields off-thread.
        private static int s_lastFrameMetricsFrame = -1;
        private static int s_lastMemoryMetricsFrame = -1;

        private void Update()
        {
            if (s_lastFrameMetricsFrame != Time.frameCount)
            {
                s_lastFrameMetricsFrame = Time.frameCount;
                PerformanceMonitor.UpdateFrameMetrics();
            }
        }

        private void LateUpdate()
        {
            if (Time.frameCount % 60 == 0 && s_lastMemoryMetricsFrame != Time.frameCount)
            {
                s_lastMemoryMetricsFrame = Time.frameCount;
                PerformanceMonitor.UpdateMemoryMetrics();
                PerformanceMonitor.UpdateGCMetrics();
            }
        }
    }
}
