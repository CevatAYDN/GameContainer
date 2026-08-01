using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Type of event in the causal trace chain.</summary>
    public enum TraceEventType
    {
        /// <summary>A signal was fired.</summary>
        Signal,
        /// <summary>A command was executed.</summary>
        Command,
        /// <summary>A model was changed.</summary>
        ModelChange,
        /// <summary>A state machine transition was attempted (Success / Superseded / Failed).</summary>
        StateTransition
    }

    /// <summary>Status of a traced event.</summary>
    public enum TraceStatus
    {
        /// <summary>The event completed successfully.</summary>
        OK,
        /// <summary>The event failed with an exception.</summary>
        Failed,
        /// <summary>The event was cancelled.</summary>
        Cancelled
    }

    /// <summary>
    /// A single event in the causal trace chain.
    /// Tracks the event ID, parent ID (for causal tree), type, timestamp, and status.
    /// </summary>
    [Preserve]
    public readonly struct TraceEvent
    {
        /// <summary>Unique event ID (monotonically increasing).</summary>
        public readonly int Id;
        /// <summary>Parent event ID in the causal chain (-1 for root events).</summary>
        public readonly int ParentId;
        /// <summary>The type of event (Signal, Command, ModelChange).</summary>
        public readonly TraceEventType Type;
        /// <summary>Timestamp from <c>Time.realtimeSinceStartupAsDouble</c>.</summary>
        public readonly double Timestamp;
        /// <summary>Type name of the signal, command, or model.</summary>
        public readonly string TypeName;
        /// <summary>Execution status (OK, Failed, Cancelled).</summary>
        public readonly TraceStatus Status;
        /// <summary>Execution mode of the command.</summary>
        public readonly ExecutionMode Mode;

        /// <summary>Creates a new <see cref="TraceEvent"/>.</summary>
        /// <param name="id">Unique event ID.</param>
        /// <param name="parentId">Parent event ID (-1 for root).</param>
        /// <param name="type">Event type.</param>
        /// <param name="timestamp">Event timestamp.</param>
        /// <param name="typeName">Type name.</param>
        /// <param name="status">Execution status.</param>
        /// <param name="mode">Execution mode.</param>
        public TraceEvent(int id, int parentId, TraceEventType type, double timestamp, string typeName, TraceStatus status, ExecutionMode mode)
        {
            Id = id;
            ParentId = parentId;
            Type = type;
            Timestamp = timestamp;
            TypeName = typeName;
            Status = status;
            Mode = mode;
        }
    }

    /// <summary>
    /// Interface for external trace event sinks.
    /// Implement to receive trace events for custom logging, visualization, or analysis.
    /// </summary>
    public interface INexusTraceSink
    {
        /// <summary>Writes a trace event to this sink.</summary>
        /// <param name="traceEvent">The event data.</param>
        void Write(in TraceEvent traceEvent);
    }

    /// <summary>
    /// High-performance causal tracing system for Nexus signal/command execution chains.
    /// Uses a ring buffer with <see cref="Interlocked"/> operations for thread safety.
    /// Compiled away when <c>NEXUS_DEBUG</c> is not defined.
    /// </summary>
    [Preserve]
    public static class NexusTrace
    {
        private const int MaxEvents = 10000;
        private static readonly TraceEvent[] s_ringBuffer = new TraceEvent[MaxEvents];
#if NEXUS_DEBUG
        private class TraceFrame
        {
            public int ParentId;
            public int BufferIndex;
            public TraceFrame Previous;
        }

        private static readonly System.Collections.Concurrent.ConcurrentBag<TraceFrame> s_framePool = new();

        private static TraceFrame RentFrame(int parentId, int bufferIndex, TraceFrame previous)
        {
            if (s_framePool.TryTake(out var frame))
            {
                frame.ParentId = parentId;
                frame.BufferIndex = bufferIndex;
                frame.Previous = previous;
                return frame;
            }
            return new TraceFrame { ParentId = parentId, BufferIndex = bufferIndex, Previous = previous };
        }

        private static void ReturnFrame(TraceFrame frame)
        {
            if (frame == null) return;
            frame.Previous = null;
            s_framePool.Add(frame);
        }

        private static int s_globalEventIdCounter = 0;
        private static int s_ringBufferIndex = -1;
        private static int s_totalEventsWritten = 0;
        private static int s_overflowWarningLogged = 0;

        private static readonly AsyncLocal<int> s_currentActiveEventId = new();
        private static readonly AsyncLocal<TraceFrame> s_currentFrame = new();
#endif

        private static readonly List<INexusTraceSink> s_sinks = new();
        private static readonly object s_lock = new();

        static NexusTrace()
        {
            Reset();
        }

        /// <summary>Resets the trace system, clearing all events and parent stacks. Called on domain reload.</summary>
        public static void Reset()
        {
            Array.Clear(s_ringBuffer, 0, s_ringBuffer.Length);
#if NEXUS_DEBUG
            s_currentFrame.Value = null;
            s_currentActiveEventId.Value = -1;
            s_ringBufferIndex = -1;
            s_globalEventIdCounter = 0;
            s_totalEventsWritten = 0;
            s_overflowWarningLogged = 0;
#endif
        }

        /// <summary>Registers an external trace sink to receive all trace events.</summary>
        /// <param name="sink">The sink implementation.</param>
        public static void AddSink(INexusTraceSink sink)
        {
            lock (s_lock)
            {
                if (!s_sinks.Contains(sink))
                {
                    s_sinks.Add(sink);
                }
            }
        }

        /// <summary>Removes a previously registered trace sink.</summary>
        /// <param name="sink">The sink to remove.</param>
        public static void RemoveSink(INexusTraceSink sink)
        {
            lock (s_lock)
            {
                s_sinks.Remove(sink);
            }
        }

        /// <summary>
        /// Begins a trace event, writing it to the ring buffer and all sinks.
        /// Thread-safe via <see cref="Interlocked"/> increment for event ID and ring buffer index.
        /// Compiled away if <c>NEXUS_DEBUG</c> is not defined.
        /// </summary>
        /// <param name="type">The type of event.</param>
        /// <param name="typeName">The type name of the signal/command/model.</param>
        /// <param name="mode">Execution mode (default: Sequential).</param>
        /// <returns>The new event ID, or 0 if tracing is disabled.</returns>
        public static int BeginEvent(TraceEventType type, string typeName, ExecutionMode mode = ExecutionMode.Sequential)
        {
#if NEXUS_DEBUG
            // High-frequency trace bypass optimization
            if (typeName == "TimerTickSignal" || typeName == "TimerCommand")
            {
                return 0;
            }

            int parentId = s_currentActiveEventId.Value;
            if (parentId == 0 && s_currentFrame.Value == null)
            {
                parentId = -1;
            }

            int eventId = Interlocked.Increment(ref s_globalEventIdCounter);

            // Compute index from the unique newIndex value, not from the shared
            // s_ringBufferIndex field. This avoids a TOCTOU race where two threads
            // read a stale index after the Interlocked.Increment.
            int rawIndex = Interlocked.Increment(ref s_ringBufferIndex);
            int index = ((rawIndex % MaxEvents) + MaxEvents) % MaxEvents;
            int totalWritten = Interlocked.Increment(ref s_totalEventsWritten);
            if (totalWritten > MaxEvents && Interlocked.CompareExchange(ref s_overflowWarningLogged, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning($"[NexusTrace] Ring buffer overflow detected. Traced events count ({totalWritten}) exceeded MaxEvents limit ({MaxEvents}). Older events are being overwritten.");
            }

            s_currentFrame.Value = RentFrame(parentId, index, s_currentFrame.Value);
            s_currentActiveEventId.Value = eventId;

            var timestamp = UnityEngine.Time.realtimeSinceStartupAsDouble;
            var traceEvent = new TraceEvent(eventId, parentId, type, timestamp, typeName, TraceStatus.OK, mode);

            s_ringBuffer[index] = traceEvent;

            lock (s_lock)
            {
                for (int i = 0; i < s_sinks.Count; i++)
                {
                    s_sinks[i].Write(traceEvent);
                }
            }

            return eventId;
#else
            return 0;
#endif
        }

        /// <summary>
        /// Ends a trace event, updating its status in the ring buffer.
        /// Thread-safe. Compiled away if <c>NEXUS_DEBUG</c> is not defined.
        /// </summary>
        /// <param name="eventId">The event ID returned by <see cref="BeginEvent"/>.</param>
        /// <param name="status">The final status (default: OK).</param>
        public static void EndEvent(int eventId, TraceStatus status = TraceStatus.OK)
        {
#if NEXUS_DEBUG
            if (eventId <= 0) return;

            var frame = s_currentFrame.Value;
            if (frame == null)
            {
                s_currentActiveEventId.Value = -1;
                return;
            }

            s_currentFrame.Value = frame.Previous;
            s_currentActiveEventId.Value = frame.ParentId;
            ReturnFrame(frame);

            int bufferIndex = frame.BufferIndex;
            if (bufferIndex >= 0 && bufferIndex < MaxEvents)
            {
                var ev = s_ringBuffer[bufferIndex];
                if (ev.Id == eventId)
                {
                    s_ringBuffer[bufferIndex] = new TraceEvent(ev.Id, ev.ParentId, ev.Type, ev.Timestamp, ev.TypeName, status, ev.Mode);
                }
            }
#endif
        }

        /// <summary>
        /// Returns recent trace events from the ring buffer in chronological order.
        /// Only available when <c>NEXUS_DEBUG</c> is defined; returns empty otherwise.
        /// </summary>
        /// <param name="count">Number of events returned.</param>
        /// <returns>Array of recent <see cref="TraceEvent"/> instances (may allocate).</returns>
        public static TraceEvent[] GetRecentEvents(out int count)
        {
#if NEXUS_DEBUG
            var events = new List<TraceEvent>();
            if (s_totalEventsWritten == 0)
            {
                count = 0;
                return Array.Empty<TraceEvent>();
            }

            int available = Math.Min(s_totalEventsWritten, MaxEvents);
            int start = s_ringBufferIndex - available + 1;
            if (start < 0) start += MaxEvents;

            for (int i = 0; i < available; i++)
            {
                int idx = (start + i) % MaxEvents;
                if (s_ringBuffer[idx].Id > 0)
                {
                    events.Add(s_ringBuffer[idx]);
                }
            }

            count = events.Count;
            return events.ToArray();
#else
            count = 0;
            return Array.Empty<TraceEvent>();
#endif
        }
    }
}
