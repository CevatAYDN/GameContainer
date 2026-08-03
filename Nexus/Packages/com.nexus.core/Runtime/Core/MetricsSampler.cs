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
        // M9 fix: add volatile to prevent stale reads under IL2CPP on ARM.
        // Multiple MetricsSampler instances (one per Root) write to these static fields;
        // without volatile, a write by one instance may not be visible to another.
        private static volatile int s_lastFrameMetricsFrame = -1;
        private static volatile int s_lastMemoryMetricsFrame = -1;

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
