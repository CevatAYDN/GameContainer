using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    public enum TraceEventType
    {
        Signal,
        Command,
        ModelChange
    }

    public enum TraceStatus
    {
        OK,
        Failed,
        Cancelled
    }

    [Preserve]
    public readonly struct TraceEvent
    {
        public readonly int Id;
        public readonly int ParentId;           // Root is -1
        public readonly TraceEventType Type;    // Signal, Command, ModelChange
        public readonly double Timestamp;
        public readonly string TypeName;
        public readonly TraceStatus Status;     // OK, Failed, Cancelled
        public readonly ExecutionMode Mode;     // Sequential, Concurrent, Exclusive, Composite

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

    public interface INexusTraceSink
    {
        void Write(in TraceEvent traceEvent);
    }

    [Preserve]
    public static class NexusTrace
    {
        private const int MaxEvents = 10000;
        private static readonly TraceEvent[] s_ringBuffer = new TraceEvent[MaxEvents];
#if NEXUS_DEBUG
        private static int s_globalEventIdCounter = 0;
        private static int s_ringBufferIndex = -1;
        private static int s_totalEventsWritten = 0;

        [ThreadStatic]
        private static int s_currentActiveEventId;
        
        [ThreadStatic]
        private static Stack<(int parentId, int bufferIndex)> s_parentStack;
#endif

        private static readonly List<INexusTraceSink> s_sinks = new();
        private static readonly object s_lock = new();

        static NexusTrace()
        {
            Reset();
        }

        public static void Reset()
        {
            Array.Clear(s_ringBuffer, 0, s_ringBuffer.Length);
#if NEXUS_DEBUG
            s_parentStack = new Stack<(int, int)>();
            s_currentActiveEventId = -1;
            s_ringBufferIndex = -1;
            s_globalEventIdCounter = 0;
            s_totalEventsWritten = 0;
#endif
        }

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

        public static void RemoveSink(INexusTraceSink sink)
        {
            lock (s_lock)
            {
                s_sinks.Remove(sink);
            }
        }

        public static int BeginEvent(TraceEventType type, string typeName, ExecutionMode mode = ExecutionMode.Sequential)
        {
#if NEXUS_DEBUG
            if (s_parentStack == null)
            {
                s_parentStack = new Stack<(int, int)>();
                s_currentActiveEventId = -1;
            }

            int parentId = s_currentActiveEventId;
            int eventId = Interlocked.Increment(ref s_globalEventIdCounter);
            
            // Advance ring buffer index with proper wrapping to prevent int overflow
            s_ringBufferIndex = (s_ringBufferIndex + 1) % MaxEvents;
            if (s_ringBufferIndex < 0) s_ringBufferIndex = 0;
            s_totalEventsWritten++;
            int index = s_ringBufferIndex;

            s_parentStack.Push((s_currentActiveEventId, index));
            s_currentActiveEventId = eventId;

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

        public static void EndEvent(int eventId, TraceStatus status = TraceStatus.OK)
        {
#if NEXUS_DEBUG
            if (s_parentStack == null || s_parentStack.Count == 0)
            {
                s_currentActiveEventId = -1;
                return;
            }

            var (parentId, bufferIndex) = s_parentStack.Pop();

            if (bufferIndex >= 0 && bufferIndex < MaxEvents)
            {
                var ev = s_ringBuffer[bufferIndex];
                if (ev.Id == eventId)
                {
                    s_ringBuffer[bufferIndex] = new TraceEvent(ev.Id, ev.ParentId, ev.Type, ev.Timestamp, ev.TypeName, status, ev.Mode);
                }
            }

            s_currentActiveEventId = parentId;
#endif
        }

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
