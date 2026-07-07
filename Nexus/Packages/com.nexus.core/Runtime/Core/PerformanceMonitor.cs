using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Performance monitoring system for Nexus Core.
    /// Tracks frame rate, CPU usage, memory allocation, and custom metrics.
    /// </summary>
    public static class PerformanceMonitor
    {
        public class MetricSample
        {
            public string Name { get; set; }
            public float Value { get; set; }
            public string Unit { get; set; }
            public DateTime Timestamp { get; set; }
            public string Category { get; set; }
        }

        private static readonly ConcurrentQueue<MetricSample> s_samples = new();
        private static readonly Dictionary<string, List<float>> s_metricHistory = new();
        private static readonly Dictionary<string, float> s_currentValues = new();
        private static int s_maxHistorySize = 300; // 5 seconds at 60fps
        private static bool s_enabled = true;
        private static bool s_recording = false;

        public static event Action<MetricSample> OnMetricRecorded;
        public static event Action OnRecordingStarted;
        public static event Action OnRecordingStopped;

        public static bool Enabled
        {
            get => s_enabled;
            set => s_enabled = value;
        }

        public static bool IsRecording => s_recording;

        public static int MaxHistorySize
        {
            get => s_maxHistorySize;
            set => s_maxHistorySize = Math.Max(10, value);
        }

        public static void StartRecording()
        {
            if (s_recording) return;
            s_recording = true;
            OnRecordingStarted?.Invoke();
        }

        public static void StopRecording()
        {
            if (!s_recording) return;
            s_recording = false;
            OnRecordingStopped?.Invoke();
        }

        public static void RecordMetric(string name, float value, string unit = "", string category = "Custom")
        {
            if (!s_enabled) return;

            var sample = new MetricSample
            {
                Name = name,
                Value = value,
                Unit = unit,
                Timestamp = DateTime.Now,
                Category = category
            };

            s_samples.Enqueue(sample);
            s_currentValues[name] = value;

            // Maintain history
            if (!s_metricHistory.ContainsKey(name))
            {
                s_metricHistory[name] = new List<float>();
            }

            s_metricHistory[name].Add(value);
            if (s_metricHistory[name].Count > s_maxHistorySize)
            {
                s_metricHistory[name].RemoveAt(0);
            }

            OnMetricRecorded?.Invoke(sample);
        }

        public static float GetMetric(string name)
        {
            return s_currentValues.TryGetValue(name, out var value) ? value : 0f;
        }

        public static float[] GetMetricHistory(string name)
        {
            return s_metricHistory.TryGetValue(name, out var history) ? history.ToArray() : Array.Empty<float>();
        }

        public static float GetMetricAverage(string name, int sampleCount = 60)
        {
            if (!s_metricHistory.TryGetValue(name, out var history)) return 0f;
            var recent = history.TakeLast(sampleCount).ToArray();
            return recent.Length > 0 ? recent.Average() : 0f;
        }

        public static float GetMetricMax(string name, int sampleCount = 60)
        {
            if (!s_metricHistory.TryGetValue(name, out var history)) return 0f;
            var recent = history.TakeLast(sampleCount).ToArray();
            return recent.Length > 0 ? recent.Max() : 0f;
        }

        public static float GetMetricMin(string name, int sampleCount = 60)
        {
            if (!s_metricHistory.TryGetValue(name, out var history)) return 0f;
            var recent = history.TakeLast(sampleCount).ToArray();
            return recent.Length > 0 ? recent.Min() : 0f;
        }

        public static MetricSample[] GetRecentSamples(int count = 100)
        {
            return s_samples.TakeLast(count).ToArray();
        }

        public static void ClearHistory()
        {
            s_metricHistory.Clear();
            s_currentValues.Clear();
            while (s_samples.TryDequeue(out _)) { }
        }

        public static void ClearMetric(string name)
        {
            if (s_metricHistory.ContainsKey(name))
            {
                s_metricHistory[name].Clear();
            }
            s_currentValues.Remove(name);
        }

        // Built-in metrics
        private static float s_lastFrameTime;
        private static int s_frameCount;

        public static void UpdateFrameMetrics()
        {
            if (!s_enabled) return;

            var deltaTime = Time.deltaTime;
            var fps = 1f / deltaTime;
            var frameTimeMs = deltaTime * 1000f;

            RecordMetric("FPS", fps, "fps", "Frame");
            RecordMetric("FrameTime", frameTimeMs, "ms", "Frame");
            RecordMetric("DeltaTime", deltaTime, "s", "Frame");

            s_frameCount++;
        }

        public static void UpdateMemoryMetrics()
        {
            if (!s_enabled) return;

            var totalMemory = System.GC.GetTotalMemory(false) / (1024f * 1024f); // MB
            var allocatedMemory = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f); // MB
            var reservedMemory = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f); // MB
            var monoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024f * 1024f); // MB
            var monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024f * 1024f); // MB

            RecordMetric("TotalMemory", totalMemory, "MB", "Memory");
            RecordMetric("AllocatedMemory", allocatedMemory, "MB", "Memory");
            RecordMetric("ReservedMemory", reservedMemory, "MB", "Memory");
            RecordMetric("MonoHeap", monoHeap, "MB", "Memory");
            RecordMetric("MonoUsed", monoUsed, "MB", "Memory");
        }

        public static void UpdateGCMetrics()
        {
            if (!s_enabled) return;

            var gen0 = System.GC.CollectionCount(0);
            var gen1 = System.GC.CollectionCount(1);
            var gen2 = System.GC.CollectionCount(2);

            RecordMetric("GC_Gen0", gen0, "count", "GC");
            RecordMetric("GC_Gen1", gen1, "count", "GC");
            RecordMetric("GC_Gen2", gen2, "count", "GC");
        }

        public static Dictionary<string, float> GetAllCurrentMetrics()
        {
            return new Dictionary<string, float>(s_currentValues);
        }

        public static string[] GetAvailableMetrics()
        {
            return s_metricHistory.Keys.ToArray();
        }
    }
}
