using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

        private const int MaxSampleQueueSize = 2000; // Bounded: prevents the samples queue from growing forever
        private static readonly ConcurrentQueue<MetricSample> s_samples = new();
        // BUG-17 fix: s_metricHistory and s_currentValues are accessed from both the game
        // thread (RecordMetric, UpdateFrameMetrics) and the editor/monitoring thread
        // (GetMetric, GetMetricHistory, GetAllCurrentMetrics). Plain Dictionary is not
        // thread-safe; protect all reads and writes with a dedicated lock.
        private static readonly Dictionary<string, Queue<float>> s_metricHistory = new();
        private static readonly Dictionary<string, float> s_currentValues = new();
        private static readonly object s_metricsLock = new();
        private static int s_maxHistorySize = 300; // 5 seconds at 60fps
        private static bool s_enabled = true;
        private static bool s_recording = false;

        public static event Action<MetricSample> OnMetricRecorded;
        public static event Action OnRecordingStarted;
        public static event Action OnRecordingStopped;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            OnMetricRecorded = null;
            OnRecordingStarted = null;
            OnRecordingStopped = null;
            s_recording = false;
            s_lastFrameMetricFrame = -1;
            lock (s_metricsLock)
            {
                s_metricHistory.Clear();
                s_currentValues.Clear();
            }
            while (s_samples.TryDequeue(out _)) { }
        }

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

            // BUG-17 fix: all dictionary reads and writes are now under s_metricsLock.
            lock (s_metricsLock)
            {
                // Always keep the latest value queryable via GetMetric, even when not recording.
                s_currentValues[name] = value;

                // History stays bounded (MaxHistorySize) regardless of recording state.
                if (!s_metricHistory.TryGetValue(name, out var history))
                {
                    history = new Queue<float>();
                    s_metricHistory[name] = history;
                }
                history.Enqueue(value);
                while (history.Count > s_maxHistorySize)
                    history.Dequeue();
            }

            // Only allocate / enqueue / notify while actively recording.
            if (!s_recording) return;

            var sample = new MetricSample
            {
                Name = name,
                Value = value,
                Unit = unit,
                Timestamp = DateTime.Now,
                Category = category
            };

            s_samples.Enqueue(sample);
            while (s_samples.Count > MaxSampleQueueSize)
                s_samples.TryDequeue(out _);

            OnMetricRecorded?.Invoke(sample);
        }

        public static float GetMetric(string name)
        {
            lock (s_metricsLock)
                return s_currentValues.TryGetValue(name, out var value) ? value : 0f;
        }

        public static float[] GetMetricHistory(string name)
        {
            lock (s_metricsLock)
                return s_metricHistory.TryGetValue(name, out var history) ? history.ToArray() : Array.Empty<float>();
        }

        public static float GetMetricAverage(string name, int sampleCount = 60)
        {
            lock (s_metricsLock)
            {
                if (!s_metricHistory.TryGetValue(name, out var history) || history.Count == 0) return 0f;
                float sum = 0f;
                int count = 0;
                int skip = Math.Max(0, history.Count - sampleCount);
                int i = 0;
                foreach (var v in history)
                {
                    if (i >= skip) { sum += v; count++; }
                    i++;
                }
                return count > 0 ? sum / count : 0f;
            }
        }

        public static float GetMetricMax(string name, int sampleCount = 60)
        {
            lock (s_metricsLock)
            {
                if (!s_metricHistory.TryGetValue(name, out var history) || history.Count == 0) return 0f;
                float max = float.MinValue;
                int skip = Math.Max(0, history.Count - sampleCount);
                int i = 0;
                foreach (var v in history)
                {
                    if (i >= skip && v > max) max = v;
                    i++;
                }
                return max;
            }
        }

        public static float GetMetricMin(string name, int sampleCount = 60)
        {
            lock (s_metricsLock)
            {
                if (!s_metricHistory.TryGetValue(name, out var history) || history.Count == 0) return 0f;
                float min = float.MaxValue;
                int skip = Math.Max(0, history.Count - sampleCount);
                int i = 0;
                foreach (var v in history)
                {
                    if (i >= skip && v < min) min = v;
                    i++;
                }
                return min;
            }
        }

        public static MetricSample[] GetRecentSamples(int count = 100)
        {
            // Snapshot the queue without LINQ — ConcurrentQueue.ToArray is O(N) but avoids
            // the intermediate IEnumerable + TakeLast iterator allocations.
            var snapshot = s_samples.ToArray();
            if (snapshot.Length <= count) return snapshot;
            var result = new MetricSample[count];
            Array.Copy(snapshot, snapshot.Length - count, result, 0, count);
            return result;
        }

        public static void ClearHistory()
        {
            lock (s_metricsLock)
            {
                s_metricHistory.Clear();
                s_currentValues.Clear();
            }
            while (s_samples.TryDequeue(out _)) { }
        }

        /// <summary>Resets the frame-metric throttle so the next UpdateFrameMetrics call records.</summary>
        internal static void ResetFrameThrottle()
        {
            s_lastFrameMetricFrame = -1;
        }

        public static void ClearMetric(string name)
        {
            lock (s_metricsLock)
            {
                if (s_metricHistory.ContainsKey(name))
                    s_metricHistory[name].Clear();
                s_currentValues.Remove(name);
            }
        }

        // Built-in metrics
        private static int s_lastFrameMetricFrame = -1;

        public static void UpdateFrameMetrics()
        {
            if (!s_enabled || !s_recording) return;

            // Throttle to ~10 Hz (6-frame cadence at 60 fps). Per-frame sampling created
            // ~180 allocations/sec of GC churn that spiked FPS every few seconds.
            // The frame-guard is armed only after the first sample so the initial call
            // always records instead of being silently dropped.
            if (s_lastFrameMetricFrame >= 0 && Time.frameCount - s_lastFrameMetricFrame < 6) return;
            s_lastFrameMetricFrame = Time.frameCount;

            var deltaTime = Time.deltaTime;
            // BUG-18 fix: deltaTime can be 0 on the first frame or during a freeze;
            // dividing by zero produces Infinity which corrupts average / max calculations.
            var fps = deltaTime > 0f ? 1f / deltaTime : 0f;
            var frameTimeMs = deltaTime * 1000f;

            RecordMetric("FPS", fps, "fps", "Frame");
            RecordMetric("FrameTime", frameTimeMs, "ms", "Frame");
            RecordMetric("DeltaTime", deltaTime, "s", "Frame");
        }

        public static void UpdateMemoryMetrics()
        {
            if (!s_enabled || !s_recording) return;

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
            if (!s_enabled || !s_recording) return;

            var gen0 = System.GC.CollectionCount(0);
            var gen1 = System.GC.CollectionCount(1);
            var gen2 = System.GC.CollectionCount(2);

            RecordMetric("GC_Gen0", gen0, "count", "GC");
            RecordMetric("GC_Gen1", gen1, "count", "GC");
            RecordMetric("GC_Gen2", gen2, "count", "GC");
        }

        public static Dictionary<string, float> GetAllCurrentMetrics()
        {
            lock (s_metricsLock)
                return new Dictionary<string, float>(s_currentValues);
        }

        public static string[] GetAvailableMetrics()
        {
            lock (s_metricsLock)
            {
                var keys = new string[s_metricHistory.Count];
                s_metricHistory.Keys.CopyTo(keys, 0);
                return keys;
            }
        }
    }
}
