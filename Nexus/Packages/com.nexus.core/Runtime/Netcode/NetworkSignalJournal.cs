using System;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Netcode
{
    internal interface IOrderedSignalHistory : INetworkSignalHistory
    {
        void ReplaySignal(long sequence, ISignalBus bus);
        void PrepareRollback(int rollbackTick, int targetTick);
        void SetRecordOrder(long sequence, int recordedTick, long order);
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
            internal readonly int RecordedTick;
            internal readonly long Order;
            internal int ActionTick => ParentId == 0 ? Math.Min(Tick, RecordedTick) : RecordedTick;
            internal Entry(int tick, long sequence, IOrderedSignalHistory history, long id, long parentId, int recordedTick, long order)
            {
                Tick = tick;
                Sequence = sequence;
                History = history;
                Id = id;
                ParentId = parentId;
                RecordedTick = recordedTick;
                Order = order;
            }
        }

        private readonly List<Entry> _entries = new(256);
        private long _nextId;

        internal long NextId() => ++_nextId;

        internal Entry Add(int tick, long sequence, IOrderedSignalHistory history, long id,
            long parentId, int recordedTick, long order)
        {
            var entry = new Entry(tick, sequence, history, id, parentId, recordedTick, order);
            _entries.Insert(FindPosition(entry), entry);
            return entry;
        }

        private int FindPosition(Entry entry)
        {
            int low = 0, high = _entries.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                var candidate = _entries[mid];
                if (candidate.ActionTick < entry.ActionTick ||
                    (candidate.ActionTick == entry.ActionTick && candidate.Id < entry.Id)) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        internal Entry SetRecordOrder(Entry entry, int recordedTick, long order)
        {
            int index = FindPosition(entry);
            if (index >= _entries.Count || _entries[index].Id != entry.Id)
                throw new InvalidOperationException("The replay input is no longer in the journal.");
            var updated = new Entry(entry.Tick, entry.Sequence, entry.History, entry.Id,
                entry.ParentId, recordedTick, order);
            _entries[index] = updated; // an external input's ActionTick does not change
            entry.History.SetRecordOrder(entry.Sequence, recordedTick, order);
            return updated;
        }

        internal void PrepareRollback(int rollbackTick, int targetTick)
        {
            int write = 0;
            for (int read = 0; read < _entries.Count; read++)
                if (_entries[read].ParentId == 0
                    ? _entries[read].Tick <= targetTick
                    : _entries[read].RecordedTick < rollbackTick)
                    _entries[write++] = _entries[read];
            _entries.RemoveRange(write, _entries.Count - write);
        }

        // Future external inputs are scheduled at their original recording tick. Late
        // external inputs execute at their target tick. Replaying only target ticks loses
        // the position of a newly generated future child relative to those schedules.
        internal int CopyReplayInputs(int firstTick, int lastTick, ref Entry[] actions, List<Entry> pending)
        {
            if (actions == null || actions.Length < _entries.Count)
                actions = new Entry[Math.Max(256, _entries.Count * 2)];
            pending.Clear();
            int count = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Tick < firstTick || entry.Tick > lastTick) continue;
                if (entry.ParentId == 0 && entry.ActionTick >= firstTick) actions[count++] = entry;
                else InsertScheduled(pending, 0, entry);
            }
            return count;
        }

        internal void Prune(int tick)
        {
            int write = 0;
            for (int read = 0; read < _entries.Count; read++)
                if (_entries[read].Tick > tick) _entries[write++] = _entries[read];
            _entries.RemoveRange(write, _entries.Count - write);
        }
        internal void Clear() => _entries.Clear();

        internal static void InsertScheduled(List<Entry> pending, int cursor, Entry entry)
        {
            int low = cursor, high = pending.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (CompareScheduled(pending[mid], entry) < 0) low = mid + 1;
                else high = mid;
            }
            pending.Insert(low, entry);
        }

        private static int CompareScheduled(Entry first, Entry second)
        {
            int result = first.Tick.CompareTo(second.Tick);
            if (result != 0) return result;
            result = first.RecordedTick.CompareTo(second.RecordedTick);
            if (result != 0) return result;
            result = first.Order.CompareTo(second.Order);
            return result != 0 ? result : first.Id.CompareTo(second.Id);
        }
    }
}
