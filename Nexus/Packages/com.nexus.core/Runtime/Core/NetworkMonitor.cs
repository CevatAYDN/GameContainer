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
        // S_latencyHistory and s_currentStatus are written from network/background
        // threads (RecordSignalReceived, UpdateConnectionStatus) and read from the game/editor
        // thread. Plain Dictionary is not thread-safe; guard ALL accesses with a dedicated lock
        // (the same BUG-17 pattern PerformanceMonitor already applies to its metric dictionaries).
        private static readonly Dictionary<string, List<float>> s_latencyHistory = new();
        private static readonly Dictionary<string, int> s_signalCounts = new();
        private static readonly object s_historyLock = new();
        private static int s_nextId = 1;
        private static int s_maxEvents = 500;
        // Volatile: s_enabled is toggled from any thread while the Record* methods read it
        // on the hot path (same rationale as PerformanceMonitor.s_enabled — a plain bool
        // could be cached in a register and never observe the toggle).
        private static volatile bool s_enabled = true;
        // True once user code has explicitly assigned Enabled; lets NexusRuntime's startup
        // default skip flags the user already chose (see NexusRuntime.InitializeMonitoring).
        private static bool s_enabledExplicitlySet;
        private static ConnectionStatus s_currentStatus = new ConnectionStatus { IsConnected = false, LastUpdate = DateTime.Now };

        public static event Action<NetworkEvent> OnNetworkEvent;
        /// <summary>
        /// Raised when the connection status changes.
        /// WARNING: may be raised off the main thread — status updates can originate from
        /// network/background threads, so subscribers must not touch Unity APIs directly.
        /// </summary>
        public static event Action<ConnectionStatus> OnConnectionStatusChanged;

        /// <summary>
        /// Domain-reload reset: clears static event subscribers so stale editor/instance
        /// handlers from a previous Play Mode run cannot be invoked in the next run.
        /// Mirrors the identical pattern in <see cref="PerformanceMonitor.ResetOnDomainReload"/>.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            OnNetworkEvent = null;
            OnConnectionStatusChanged = null;
            s_enabled = true;
            s_enabledExplicitlySet = false;
            s_nextId = 1;
            s_currentStatus = new ConnectionStatus { IsConnected = false, LastUpdate = DateTime.Now };
            lock (s_historyLock)
            {
                s_latencyHistory.Clear();
            }
            lock (s_signalCounts)
            {
                s_signalCounts.Clear();
            }
            while (s_events.TryDequeue(out _)) { }
        }


        public static bool Enabled
        {
            get => s_enabled;
            set
            {
                s_enabledExplicitlySet = true;
                s_enabled = value;
            }
        }

        /// <summary>True once <see cref="Enabled"/> has been explicitly assigned.</summary>
        internal static bool EnabledExplicitlySet => s_enabledExplicitlySet;

        public static int MaxEvents
        {
            get => s_maxEvents;
            set => s_maxEvents = Math.Max(100, value);
        }

        /// <summary>
        /// Returns a defensive snapshot of the current connection status.
        /// Callers must not mutate the returned instance; it is a copy so readers on
        /// any thread never observe a torn write from a concurrent status update.
        /// </summary>
        public static ConnectionStatus CurrentStatus
        {
            get
            {
                lock (s_historyLock)
                {
                    return SnapshotStatus();
                }
            }
        }

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
            RaiseNetworkEvent(evt);

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
            RaiseNetworkEvent(evt);

            // Update status with latest latency under the lock; invoke the event with a
            // snapshot so subscribers never read a partially-updated status object.
            ConnectionStatus snapshot;
            lock (s_historyLock)
            {
                s_currentStatus.LatencyMs = latencyMs;
                s_currentStatus.LastUpdate = DateTime.Now;
                snapshot = SnapshotStatus();
            }
            RaiseStatusChanged(snapshot);

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
            RaiseNetworkEvent(evt);

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
            RaiseNetworkEvent(evt);

            PruneOldEvents();
        }

        public static void UpdateConnectionStatus(bool isConnected, string connectionType = null, float packetLoss = 0f, float bandwidthKbps = 0f)
        {
            ConnectionStatus snapshot;
            lock (s_historyLock)
            {
                s_currentStatus.IsConnected = isConnected;
                s_currentStatus.ConnectionType = connectionType ?? s_currentStatus.ConnectionType;
                s_currentStatus.PacketLoss = packetLoss;
                s_currentStatus.BandwidthKbps = bandwidthKbps;
                s_currentStatus.LastUpdate = DateTime.Now;
                snapshot = SnapshotStatus();
            }
            RaiseStatusChanged(snapshot);
        }

        /// <summary>
        /// Builds a defensive copy of the current status. Must be called with
        /// <see cref="s_historyLock"/> held so readers and event subscribers never observe
        /// (or mutate) the shared status object. Single construction site so adding a field
        /// to <see cref="ConnectionStatus"/> cannot drift across the read/write paths.
        /// </summary>
        private static ConnectionStatus SnapshotStatus()
        {
            return new ConnectionStatus
            {
                IsConnected = s_currentStatus.IsConnected,
                ConnectionType = s_currentStatus.ConnectionType,
                LatencyMs = s_currentStatus.LatencyMs,
                PacketLoss = s_currentStatus.PacketLoss,
                BandwidthKbps = s_currentStatus.BandwidthKbps,
                LastUpdate = s_currentStatus.LastUpdate
            };
        }

        private static void RecordLatency(string signalName, float latencyMs)
        {
            lock (s_historyLock)
            {
                if (!s_latencyHistory.TryGetValue(signalName, out var history))
                {
                    history = new List<float>(8);
                    s_latencyHistory[signalName] = history;
                }

                history.Add(latencyMs);
                if (history.Count > 100)
                {
                    history.RemoveAt(0);
                }
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
            lock (s_historyLock)
            {
                if (!s_latencyHistory.TryGetValue(signalName, out var history)) return 0f;
                var recent = history.TakeLast(sampleCount).ToArray();
                return recent.Length > 0 ? recent.Average() : 0f;
            }
        }

        public static float GetMaxLatency(string signalName, int sampleCount = 30)
        {
            lock (s_historyLock)
            {
                if (!s_latencyHistory.TryGetValue(signalName, out var history)) return 0f;
                var recent = history.TakeLast(sampleCount).ToArray();
                return recent.Length > 0 ? recent.Max() : 0f;
            }
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
            lock (s_historyLock)
            {
                s_latencyHistory.Clear();
            }
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

        // ── Safe event raising (M7) ────────────────────────────────────────────
        // Subscribers are third-party code; a throwing subscriber must not propagate out of
        // the record/update methods (which run on network/background threads inside signal
        // paths) nor prevent other subscribers from receiving the event. Each subscriber
        // runs in its own try/catch and failures are logged — never silent, never fatal.

        private static void RaiseNetworkEvent(NetworkEvent evt)
        {
            var handler = OnNetworkEvent;
            if (handler == null) return;
            foreach (Action<NetworkEvent> subscriber in handler.GetInvocationList())
            {
                try { subscriber(evt); }
                catch (Exception ex)
                {
                    Debug.LogError($"[Nexus] OnNetworkEvent subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void RaiseStatusChanged(ConnectionStatus status)
        {
            var handler = OnConnectionStatusChanged;
            if (handler == null) return;
            foreach (Action<ConnectionStatus> subscriber in handler.GetInvocationList())
            {
                try { subscriber(status); }
                catch (Exception ex)
                {
                    Debug.LogError($"[Nexus] OnConnectionStatusChanged subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
