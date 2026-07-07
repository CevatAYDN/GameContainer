using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Network monitoring system for Nexus Core.
    /// Tracks network signal latency, connection status, and network errors.
    /// </summary>
    public static class NetworkMonitor
    {
        public class NetworkEvent
        {
            public int Id { get; set; }
            public DateTime Timestamp { get; set; }
            public string SignalName { get; set; }
            public string EventType { get; set; } // "Sent", "Received", "Failed", "Timeout"
            public float LatencyMs { get; set; }
            public int Bytes { get; set; }
            public string Error { get; set; }
            public string Source { get; set; }
            public string Destination { get; set; }
        }

        public class ConnectionStatus
        {
            public bool IsConnected { get; set; }
            public string ConnectionType { get; set; }
            public float LatencyMs { get; set; }
            public float PacketLoss { get; set; }
            public float BandwidthKbps { get; set; }
            public DateTime LastUpdate { get; set; }
        }

        private static readonly ConcurrentQueue<NetworkEvent> s_events = new();
        private static readonly Dictionary<string, List<float>> s_latencyHistory = new();
        private static readonly Dictionary<string, int> s_signalCounts = new();
        private static int s_nextId = 1;
        private static int s_maxEvents = 500;
        private static bool s_enabled = true;
        private static ConnectionStatus s_currentStatus = new ConnectionStatus { IsConnected = false, LastUpdate = DateTime.Now };

        public static event Action<NetworkEvent> OnNetworkEvent;
        public static event Action<ConnectionStatus> OnConnectionStatusChanged;

        public static bool Enabled
        {
            get => s_enabled;
            set => s_enabled = value;
        }

        public static int MaxEvents
        {
            get => s_maxEvents;
            set => s_maxEvents = Math.Max(100, value);
        }

        public static ConnectionStatus CurrentStatus => s_currentStatus;

        public static void RecordSignalSent(string signalName, int bytes = 0, string destination = null)
        {
            if (!s_enabled) return;

            var evt = new NetworkEvent
            {
                Id = System.Threading.Interlocked.Increment(ref s_nextId),
                Timestamp = DateTime.Now,
                SignalName = signalName,
                EventType = "Sent",
                Bytes = bytes,
                Destination = destination
            };

            s_events.Enqueue(evt);
            IncrementSignalCount(signalName);
            OnNetworkEvent?.Invoke(evt);

            PruneOldEvents();
        }

        public static void RecordSignalReceived(string signalName, float latencyMs, int bytes = 0, string source = null)
        {
            if (!s_enabled) return;

            var evt = new NetworkEvent
            {
                Id = System.Threading.Interlocked.Increment(ref s_nextId),
                Timestamp = DateTime.Now,
                SignalName = signalName,
                EventType = "Received",
                LatencyMs = latencyMs,
                Bytes = bytes,
                Source = source
            };

            s_events.Enqueue(evt);
            RecordLatency(signalName, latencyMs);
            OnNetworkEvent?.Invoke(evt);

            // Update status with latest latency
            s_currentStatus.LatencyMs = latencyMs;
            s_currentStatus.LastUpdate = DateTime.Now;
            OnConnectionStatusChanged?.Invoke(s_currentStatus);

            PruneOldEvents();
        }

        public static void RecordSignalFailed(string signalName, string error, string context = null)
        {
            if (!s_enabled) return;

            var evt = new NetworkEvent
            {
                Id = System.Threading.Interlocked.Increment(ref s_nextId),
                Timestamp = DateTime.Now,
                SignalName = signalName,
                EventType = "Failed",
                Error = error,
                Destination = context
            };

            s_events.Enqueue(evt);
            OnNetworkEvent?.Invoke(evt);

            PruneOldEvents();
        }

        public static void RecordSignalTimeout(string signalName, float timeoutMs, string context = null)
        {
            if (!s_enabled) return;

            var evt = new NetworkEvent
            {
                Id = System.Threading.Interlocked.Increment(ref s_nextId),
                Timestamp = DateTime.Now,
                SignalName = signalName,
                EventType = "Timeout",
                LatencyMs = timeoutMs,
                Error = "Timeout",
                Destination = context
            };

            s_events.Enqueue(evt);
            OnNetworkEvent?.Invoke(evt);

            PruneOldEvents();
        }

        public static void UpdateConnectionStatus(bool isConnected, string connectionType = null, float packetLoss = 0f, float bandwidthKbps = 0f)
        {
            s_currentStatus.IsConnected = isConnected;
            s_currentStatus.ConnectionType = connectionType ?? s_currentStatus.ConnectionType;
            s_currentStatus.PacketLoss = packetLoss;
            s_currentStatus.BandwidthKbps = bandwidthKbps;
            s_currentStatus.LastUpdate = DateTime.Now;

            OnConnectionStatusChanged?.Invoke(s_currentStatus);
        }

        private static void RecordLatency(string signalName, float latencyMs)
        {
            if (!s_latencyHistory.ContainsKey(signalName))
            {
                s_latencyHistory[signalName] = new List<float>();
            }

            s_latencyHistory[signalName].Add(latencyMs);
            if (s_latencyHistory[signalName].Count > 100)
            {
                s_latencyHistory[signalName].RemoveAt(0);
            }
        }

        private static void IncrementSignalCount(string signalName)
        {
            lock (s_signalCounts)
            {
                if (!s_signalCounts.ContainsKey(signalName))
                {
                    s_signalCounts[signalName] = 0;
                }
                s_signalCounts[signalName]++;
            }
        }

        private static void PruneOldEvents()
        {
            while (s_events.Count > s_maxEvents)
            {
                s_events.TryDequeue(out _);
            }
        }

        public static NetworkEvent[] GetRecentEvents(int count = 50)
        {
            return s_events.TakeLast(count).ToArray();
        }

        public static NetworkEvent[] GetEventsBySignal(string signalName, int count = 50)
        {
            return s_events.Where(e => e.SignalName == signalName).TakeLast(count).ToArray();
        }

        public static NetworkEvent[] GetFailedEvents(int count = 50)
        {
            return s_events.Where(e => e.EventType == "Failed" || e.EventType == "Timeout").TakeLast(count).ToArray();
        }

        public static float GetAverageLatency(string signalName, int sampleCount = 30)
        {
            if (!s_latencyHistory.TryGetValue(signalName, out var history)) return 0f;
            var recent = history.TakeLast(sampleCount).ToArray();
            return recent.Length > 0 ? recent.Average() : 0f;
        }

        public static float GetMaxLatency(string signalName, int sampleCount = 30)
        {
            if (!s_latencyHistory.TryGetValue(signalName, out var history)) return 0f;
            var recent = history.TakeLast(sampleCount).ToArray();
            return recent.Length > 0 ? recent.Max() : 0f;
        }

        public static Dictionary<string, int> GetSignalCounts()
        {
            lock (s_signalCounts)
            {
                return new Dictionary<string, int>(s_signalCounts);
            }
        }

        public static void ClearHistory()
        {
            s_events.Clear();
            s_latencyHistory.Clear();
            lock (s_signalCounts)
            {
                s_signalCounts.Clear();
            }
        }

        public static float GetTotalBytesSent()
        {
            return s_events.Where(e => e.EventType == "Sent").Sum(e => e.Bytes);
        }

        public static float GetTotalBytesReceived()
        {
            return s_events.Where(e => e.EventType == "Received").Sum(e => e.Bytes);
        }

        public static int GetFailedEventCount()
        {
            return s_events.Count(e => e.EventType == "Failed" || e.EventType == "Timeout");
        }
    }
}
