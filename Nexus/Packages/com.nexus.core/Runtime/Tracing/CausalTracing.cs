using System;
using System.Collections.Generic;
using System.Threading;

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

    public static class NexusTrace
    {
        private const int MaxEvents = 10000;
        private static readonly TraceEvent[] s_ringBuffer = new TraceEvent[MaxEvents];
        private static int s_globalEventIdCounter = 0;
        private static int s_ringBufferIndex = -1;

        [ThreadStatic]
        private static int s_currentActiveEventId;
        
        [ThreadStatic]
        private static Stack<int> s_parentIdStack;

        private static readonly List<INexusTraceSink> s_sinks = new();
        private static readonly object s_lock = new();

        static NexusTrace()
        {
            Reset();
        }

        public static void Reset()
        {
            s_parentIdStack = new Stack<int>();
            s_currentActiveEventId = -1;
            s_ringBufferIndex = -1;
            Array.Clear(s_ringBuffer, 0, s_ringBuffer.Length);
            s_globalEventIdCounter = 0;
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
            if (s_parentIdStack == null)
            {
                s_parentIdStack = new Stack<int>();
                s_currentActiveEventId = -1;
            }

            int parentId = s_currentActiveEventId;
            int eventId = Interlocked.Increment(ref s_globalEventIdCounter);

            s_parentIdStack.Push(s_currentActiveEventId);
            s_currentActiveEventId = eventId;

            var timestamp = UnityEngine.Time.realtimeSinceStartupAsDouble;
            var traceEvent = new TraceEvent(eventId, parentId, type, timestamp, typeName, TraceStatus.OK, mode);

            // Save to ring buffer
            int index = Interlocked.Increment(ref s_ringBufferIndex) % MaxEvents;
            if (index < 0) index = 0;
            s_ringBuffer[index] = traceEvent;

            // Notify sinks
            lock (s_lock)
            {
                for (int i = 0; i < s_sinks.Count; i++)
                {
                    s_sinks[i].Write(traceEvent);
                }
            }

            return eventId;
        }

        public static void EndEvent(int eventId, TraceStatus status = TraceStatus.OK)
        {
            if (s_parentIdStack == null || s_parentIdStack.Count == 0)
            {
                s_currentActiveEventId = -1;
                return;
            }

            // Update status in ring buffer for this event
            int index = s_ringBufferIndex;
            for (int i = 0; i < MaxEvents; i++)
            {
                int idx = (index - i) % MaxEvents;
                if (idx < 0) idx += MaxEvents;

                if (s_ringBuffer[idx].Id == eventId)
                {
                    var ev = s_ringBuffer[idx];
                    s_ringBuffer[idx] = new TraceEvent(ev.Id, ev.ParentId, ev.Type, ev.Timestamp, ev.TypeName, status, ev.Mode);
                    break;
                }
            }

            s_currentActiveEventId = s_parentIdStack.Pop();
        }

        public static TraceEvent[] GetRecentEvents(out int count)
        {
            var events = new List<TraceEvent>();
            int lastIndex = s_ringBufferIndex;
            if (lastIndex == -1)
            {
                count = 0;
                return Array.Empty<TraceEvent>();
            }

            int start = Math.Max(0, lastIndex - MaxEvents + 1);
            for (int i = start; i <= lastIndex; i++)
            {
                var idx = i % MaxEvents;
                if (s_ringBuffer[idx].Id > 0)
                {
                    events.Add(s_ringBuffer[idx]);
                }
            }

            count = events.Count;
            return events.ToArray();
        }
    }
}
