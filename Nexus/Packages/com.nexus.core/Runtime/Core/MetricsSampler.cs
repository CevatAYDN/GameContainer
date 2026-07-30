using UnityEngine;

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
