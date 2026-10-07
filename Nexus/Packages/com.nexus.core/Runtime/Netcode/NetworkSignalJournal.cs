using System;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Netcode
{
    internal interface IOrderedSignalHistory
    {
        void ReplaySignal(long sequence, ISignalBus bus);
    }

    /// <summary>Cross-type ordering without boxing payloads. Owned by the bus tick lock.</summary>
    internal sealed class NetworkSignalJournal
    {
        internal readonly struct Entry
        {
            internal readonly int Tick;
            internal readonly long Sequence;
            internal readonly IOrderedSignalHistory History;
            internal readonly long Id, ParentId;
            internal Entry(int tick, long sequence, IOrderedSignalHistory history, long id, long parentId)
            {
                Tick = tick;
                Sequence = sequence;
                History = history;
                Id = id;
                ParentId = parentId;
            }
        }

        private readonly List<Entry> _entries = new(256);
        private readonly HashSet<long> _applied = new();
        private long _nextId;

        internal Entry Add(int tick, long sequence, IOrderedSignalHistory history, long parentId = 0)
        {
            // Usually append. Late inputs are inserted after all existing entries at
            // their tick, preserving recording order across different signal types.
            int index = UpperBound(tick);
            var entry = new Entry(tick, sequence, history, ++_nextId, parentId);
            _entries.Insert(index, entry);
            return entry;
        }

        internal int CopyRange(int firstTick, int lastTick, ref Entry[] buffer)
        {
            int first = LowerBound(firstTick);
            int count = UpperBound(lastTick) - first;
            if (buffer == null || buffer.Length < count)
                buffer = new Entry[Math.Max(256, count * 2)];
            _entries.CopyTo(first, buffer, 0, count);
            return count;
        }

        internal void RemoveAfter(int tick)
        {
            int first = UpperBound(tick);
            for (int i = first; i < _entries.Count; i++) _applied.Remove(_entries[i].Id);
            _entries.RemoveRange(first, _entries.Count - first);
        }

        internal bool WasApplied(long id) => _applied.Contains(id);
        internal void MarkApplied(long id) => _applied.Add(id);
        internal void Prune(int tick)
        {
            int count = UpperBound(tick);
            for (int i = 0; i < count; i++) _applied.Remove(_entries[i].Id);
            _entries.RemoveRange(0, count);
        }
        internal void Clear() { _entries.Clear(); _applied.Clear(); }

        private int LowerBound(int tick)
        {
            int low = 0, high = _entries.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (_entries[mid].Tick < tick) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private int UpperBound(int tick)
        {
            int low = 0, high = _entries.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (_entries[mid].Tick <= tick) low = mid + 1;
                else high = mid;
            }
            return low;
        }
    }
}
