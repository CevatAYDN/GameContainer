using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nexus.Core;

namespace Nexus.Netcode
{
    /// <summary>
    /// Marker interface for signals that can be replicated and serialized over the network.
    /// </summary>
    public interface INetworkSignal
    {
    }

    /// <summary>
    /// Non-generic interface to allow NetworkSignalBus to manage multiple snapshot handlers dynamically.
    /// </summary>
    public interface INetworkModelSnapshotHandler
    {
        void Capture(int tick);
        /// <summary>True when a snapshot of <paramref name="tick"/> is kept, so <see cref="Restore"/> can return to it.</summary>
        bool HasSnapshot(int tick);
        void Restore(int tick);
        void Prune(int confirmedTick);
    }

    internal interface INetworkSnapshotHistory : INetworkModelSnapshotHandler
    {
        void InvalidateAfter(int tick);
    }

    /// <summary>
    /// Generic implementation wrapper for snapshot handlers.
    /// </summary>
    public class NetworkModelSnapshotHandler<TState> : INetworkSnapshotHistory where TState : struct
    {
        private readonly ISnapshotableModel<TState> _model;
        private readonly Dictionary<int, TState> _snapshots = new();
        // Reusable list to avoid allocating a new List per Prune call.
        private readonly List<int> _keysToPrune = new();

        public NetworkModelSnapshotHandler(ISnapshotableModel<TState> model)
        {
            _model = model;
        }

        public void Capture(int tick)
        {
            _snapshots[tick] = _model.CaptureSnapshot();
        }

        public bool HasSnapshot(int tick) => _snapshots.ContainsKey(tick);

        public void Restore(int tick)
        {
            // a missing snapshot left the model at its current state, under a replay of inputs it had already applied (#170)
            if (!_snapshots.TryGetValue(tick, out var state))
                throw new InvalidOperationException($"No snapshot of tick {tick} for {_model.GetType().Name}.");
            _model.RestoreSnapshot(state);
        }

        public void Prune(int confirmedTick) => RemoveSnapshots(confirmedTick, newer: false);

        void INetworkSnapshotHistory.InvalidateAfter(int tick) => RemoveSnapshots(tick, newer: true);

        private void RemoveSnapshots(int tick, bool newer)
        {
            _keysToPrune.Clear();
            foreach (var kvp in _snapshots)
            {
                if (newer ? kvp.Key > tick : kvp.Key <= tick) _keysToPrune.Add(kvp.Key);
            }
            for (int i = 0; i < _keysToPrune.Count; i++)
            {
                _snapshots.Remove(_keysToPrune[i]);
            }
        }
    }

    public interface INetworkSignalHistory
    {
        void ReplaySignals(int tick, ISignalBus localSignalBus);
        void RemoveSignalsAfter(int tick);
        void Prune(int confirmedTick);
        void Clear();
    }

    public struct BufferedNetworkSignal<T> where T : struct
    {
        public int Tick;
        public T Signal;
        internal long Sequence;
        internal bool IsDerived;
        internal int RecordedTick;
        internal long Order;
    }

    public class NetworkSignalHistory<T> : INetworkSignalHistory, IOrderedSignalHistory where T : struct, INetworkSignal
    {
        private readonly List<BufferedNetworkSignal<T>> _signals;
        private readonly object _signalsLock = new();
        private readonly List<List<BufferedNetworkSignal<T>>> _replayBuffers = new();
        private readonly object _replayLock = new();
        private int _replayDepth;
        private long _nextSequence;
        private static readonly Comparison<BufferedNetworkSignal<T>> s_recordOrder = CompareRecordOrder;

        private static int CompareRecordOrder(BufferedNetworkSignal<T> first, BufferedNetworkSignal<T> second)
        {
            int result = first.RecordedTick.CompareTo(second.RecordedTick);
            if (result != 0) return result;
            result = first.Order.CompareTo(second.Order);
            return result != 0 ? result : first.Sequence.CompareTo(second.Sequence);
        }

        // Return a stable snapshot for readers.  Add/rollback/prune are synchronized below;
        // exposing List<T>.AsReadOnly() directly would still let an external enumerator race
        // with in-place compaction.  This property is an inspection API, not the Fire hot path.
        public IReadOnlyList<BufferedNetworkSignal<T>> Signals
        {
            get
            {
                lock (_signalsLock)
                {
                    var snapshot = _signals.ToArray();
                    Array.Sort(snapshot, s_recordOrder);
                    return new System.Collections.ObjectModel.ReadOnlyCollection<BufferedNetworkSignal<T>>(snapshot);
                }
            }
        }

        public NetworkSignalHistory(int initialCapacity = 256)
        {
            int capacity = Math.Max(1, initialCapacity);
            _signals = new List<BufferedNetworkSignal<T>>(capacity);
            _replayBuffers.Add(new List<BufferedNetworkSignal<T>>(capacity));
        }

        public void Add(int tick, T signal)
            => AddTracked(tick, signal);

        internal long AddTracked(int tick, T signal, bool isDerived = false, int recordedTick = 0, long order = 0)
        {
            lock (_signalsLock)
            {
                long sequence = ++_nextSequence;
                _signals.Add(new BufferedNetworkSignal<T>
                {
                    Tick = tick, Signal = signal, Sequence = sequence, IsDerived = isDerived,
                    RecordedTick = recordedTick, Order = order == 0 ? sequence : order
                });
                return sequence;
            }
        }

        void IOrderedSignalHistory.ReplaySignal(long sequence, ISignalBus bus)
        {
            if (TryReadSignal(sequence, out var signal)) DispatchReplay(signal, bus);
        }

        void IOrderedSignalHistory.PrepareRollback(int rollbackTick, int targetTick)
        {
            lock (_signalsLock)
            {
                int write = 0;
                for (int read = 0; read < _signals.Count; read++)
                    if (_signals[read].IsDerived
                        ? _signals[read].RecordedTick < rollbackTick
                        : _signals[read].Tick <= targetTick)
                        _signals[write++] = _signals[read];
                _signals.RemoveRange(write, _signals.Count - write);
            }
        }

        void IOrderedSignalHistory.SetRecordOrder(long sequence, int recordedTick, long order)
        {
            lock (_signalsLock)
            {
                int index = FindSequenceLocked(sequence);
                if (index < 0) return;
                var entry = _signals[index];
                entry.RecordedTick = recordedTick;
                entry.Order = order;
                _signals[index] = entry;
            }
        }

        private bool TryReadSignal(long sequence, out T signal)
        {
            lock (_signalsLock)
            {
                int index = FindSequenceLocked(sequence);
                signal = index >= 0 ? _signals[index].Signal : default;
                return index >= 0;
            }
        }

        private int FindSequenceLocked(long sequence)
        {
            int low = 0, high = _signals.Count - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                long candidate = _signals[mid].Sequence;
                if (candidate < sequence) low = mid + 1;
                else if (candidate > sequence) high = mid - 1;
                else return mid;
            }
            return -1;
        }

        private static void DispatchReplay(T signal, ISignalBus bus)
        {
            try { bus.Fire(signal); }
            catch (NexusSyncAsyncMismatchException)
            {
                NexusRuntime.Logger?.LogError($"[NetworkSignalBus] Signal '{typeof(T).FullName}' has async handlers; its snapshots are not rollback-safe.");
                if (bus is SignalBus concreteBus) concreteBus.FireQueued(signal);
            }
        }

        /// <summary>
        /// Replays every buffered signal recorded at the given tick through the local bus.
        /// B8 contract: signals are dispatched SYNCHRONOUSLY in-record-order so the caller's
        /// per-tick snapshot capture observes the fully resimulated state. Signals with async
        /// handlers cannot be replayed deterministically — a clear error is logged and the
        /// signal falls back to fire-and-forget <see cref="SignalBus.FireQueued"/> dispatch
        /// (snapshots for such signals are NOT rollback-safe).
        /// </summary>
        public void ReplaySignals(int tick, ISignalBus localSignalBus)
        {
            // The monitor serializes threads, not recursive calls. Each active depth owns
            // its snapshot until finally, including when a callback clears/prunes history (#344).
            lock (_replayLock)
            {
                int depth = _replayDepth++;
                List<BufferedNetworkSignal<T>> buffer = null;
                try
                {
                    lock (_signalsLock)
                    {
                        if (_replayBuffers.Count == depth)
                            _replayBuffers.Add(new List<BufferedNetworkSignal<T>>(_signals.Capacity));
                        buffer = _replayBuffers[depth];
                        for (int i = 0; i < _signals.Count; i++)
                            if (_signals[i].Tick == tick) buffer.Add(_signals[i]);
                    }

                    buffer.Sort(s_recordOrder);
                    for (int i = 0; i < buffer.Count; i++) DispatchReplay(buffer[i].Signal, localSignalBus);
                }
                finally
                {
                    buffer?.Clear();
                    _replayDepth--;
                }
            }
        }

        public void RemoveSignalsAfter(int tick)
        {
            // In-place compaction: single O(N) pass, no new list. Repeated RemoveAt
            // in the old backwards loop was O(N²) for large histories (each removal
            // shifts every later element). List.RemoveAll would allocate a predicate;
            // manual compaction keeps it at 0 B (Netcode_Rollback_And_Replay_ZeroGC).
            lock (_signalsLock)
            {
                int write = 0;
                for (int read = 0; read < _signals.Count; read++)
                {
                    if (_signals[read].Tick <= tick)
                    {
                        _signals[write] = _signals[read];
                        write++;
                    }
                }
                if (write < _signals.Count)
                {
                    _signals.RemoveRange(write, _signals.Count - write);
                }
            }
        }

        public void Prune(int confirmedTick)
        {
            // Same O(N) in-place compaction as RemoveSignalsAfter —
            // keeps only signals strictly newer than the confirmed tick.
            lock (_signalsLock)
            {
                int write = 0;
                for (int read = 0; read < _signals.Count; read++)
                {
                    if (_signals[read].Tick > confirmedTick)
                    {
                        _signals[write] = _signals[read];
                        write++;
                    }
                }
                if (write < _signals.Count)
                {
                    _signals.RemoveRange(write, _signals.Count - write);
                }
            }
        }

        public void Clear()
        {
            lock (_signalsLock) _signals.Clear();
        }
    }

    /// <summary>
    /// Network-aware Signal Bus wrapper supporting rollback simulation, tick-based buffering,
    /// and deterministic replay for multiplayer games.
    /// </summary>
    public class NetworkSignalBus
    {
        private readonly ISignalBus _localSignalBus;
        // ConcurrentDictionary so concurrent Fire<T> calls from different threads
        // (the bus is documented as network/rollback-aware and uses volatile tick state)
        // can never corrupt the history map. The old plain Dictionary's
        // TryGetValue + indexer write was a torn-read/write race under concurrent access.
        private readonly ConcurrentDictionary<Type, INetworkSignalHistory> _histories = new();
        private readonly List<IOrderedSignalHistory> _historyList = new(); // owned by _tickLock
        private System.Collections.ObjectModel.ReadOnlyDictionary<Type, INetworkSignalHistory> _historiesReadOnly;
        private readonly List<INetworkSnapshotHistory> _modelHandlers = new();
        // _modelHandlers is registered from setup code but iterated from tick/rollback paths;
        // guarded by a lock to match the concurrent design of _histories.
        private readonly object _modelHandlersLock = new();
        private volatile int _currentTick;

        // Replay retains external inputs and regenerates handler-derived events.
        private volatile bool _isResimulating;
        private readonly int _ownerThreadId;
        private readonly object _tickLock = new();
        private readonly NetworkSignalJournal _journal = new();
        private NetworkSignalJournal.Entry[] _replayActions;
        private int _replayDispatchDepth;
        private long _currentEventId;
        private int _queueEpoch;
        private NetworkInputBatch _pendingInputs = new();
        private long _recordOrder;
        private int _replayTarget, _generatedCursor;
        private readonly List<NetworkSignalJournal.Entry> _generatedInputs = new();

        public int CurrentTick => _currentTick;
        // Read-only live wrapper — prevents callers from casting back to the mutable dictionary.
        public IReadOnlyDictionary<Type, INetworkSignalHistory> Histories =>
            _historiesReadOnly ??= new System.Collections.ObjectModel.ReadOnlyDictionary<Type, INetworkSignalHistory>(_histories);

        public NetworkSignalBus(ISignalBus localSignalBus)
        {
            _localSignalBus = localSignalBus;
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private NetworkSignalHistory<T> GetOrCreateHistory<T>() where T : struct, INetworkSignal
        {
            var type = typeof(T);
            // All writers hold _tickLock. Keep a list for lifecycle
            // iteration; ConcurrentDictionary.Values materializes a collection each call.
            if (_histories.TryGetValue(type, out var existing)) return (NetworkSignalHistory<T>)existing;
            var history = new NetworkSignalHistory<T>();
            _histories[type] = history;
            _historyList.Add(history);
            return history;
        }

        /// <summary>
        /// Registers a snapshotable model to be tracked for rollback states.
        /// </summary>
        public void RegisterModel<TState>(ISnapshotableModel<TState> model) where TState : struct
        {
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("Cannot register a model during rollback.");
                lock (_modelHandlersLock) _modelHandlers.Add(new NetworkModelSnapshotHandler<TState>(model));
            }
        }

        /// <summary>
        /// Updates the current simulation tick.
        /// </summary>
        public void SetTick(int tick)
        {
            // Capture the pre-tick model state before publishing the new tick.  Fire() takes
            // the same lock while reading the tick and appending history, so a worker cannot
            // associate a signal with a tick whose snapshot is still being captured.
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("Cannot change the tick during rollback.");
                if (Volatile.Read(ref _pendingInputs.Count) != 0) throw new InvalidOperationException("Drain incoming signals before changing the tick.");
                lock (_modelHandlersLock)
                {
                    for (int i = 0; i < _modelHandlers.Count; i++)
                    {
                        _modelHandlers[i].Capture(tick);
                    }
                }
                _currentTick = tick;
            }
        }

        /// <summary>
        /// Fires a signal from the owning thread immediately and registers it in the tick
        /// history.  Calls from worker threads are marshaled through FireThreadSafe so handlers
        /// never execute on a network worker.  Async handlers use the queued async-safe path.
        /// </summary>
        public void Fire<T>(T signal) where T : struct, INetworkSignal
        {
            NetworkSignalJournal.Entry recorded = default;
            bool owner = Thread.CurrentThread.ManagedThreadId == _ownerThreadId;
            int epoch = 0;
            lock (_tickLock)
            {
                if (_isResimulating && !owner)
                    throw new InvalidOperationException("Submit incoming network signals before rollback or after it completes.");
                if (_isResimulating && _replayDispatchDepth == 0) return; // snapshot callback, not a replayed handler
                if (!owner && !(_localSignalBus is SignalBus))
                    throw new NotSupportedException("Worker network inputs require Nexus SignalBus. Use the owner thread with a custom signal bus.");
                if (owner && _currentEventId == 0 && Volatile.Read(ref _pendingInputs.Count) != 0)
                    throw new InvalidOperationException("Drain incoming signals before an owner-thread fire.");
                recorded = Record(signal, _currentTick, owner ? _currentEventId : 0);
                if (!owner)
                {
                    epoch = _queueEpoch;
                    Interlocked.Increment(ref _pendingInputs.Count);
                    // Record and enqueue share one ordering boundary across producers.
                    ((SignalBus)_localSignalBus).EnqueueDispatch(QueuedNetworkInput<T>.Rent(this, signal, recorded.Id, epoch, _pendingInputs));
                }
            }

            if (!owner)
            {
                // Network callbacks may arrive on worker threads.  Always marshal through the
                // public thread-safe queue; calling FireQueued directly would execute sync
                // handlers on the worker thread instead of the Unity main-thread drain.
                return;
            }
            if (_isResimulating) ReplayEntry(recorded);
            else DispatchLive(signal, recorded.Id);
        }

        private void DispatchLive<T>(T signal, long eventId) where T : struct, INetworkSignal
        {
            long previous = _currentEventId;
            _currentEventId = eventId;
            try
            {
                // Preserve the inline owner-thread fast path.  Async handlers still use
                // the queued async-safe dispatcher; sync-only handlers run inline.
                if (_localSignalBus is SignalBus concreteBus && concreteBus.HasAsyncHandlers(typeof(T)))
                    concreteBus.FireQueued(signal);
                else
                    _localSignalBus.Fire(signal);
            }
            finally { _currentEventId = previous; }
        }

        internal void DispatchQueued<T>(T signal, long eventId, int epoch) where T : struct, INetworkSignal
        {
            if (epoch != Volatile.Read(ref _queueEpoch)) return;
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Drain network inputs on the owning thread.");
            DispatchLive(signal, eventId);
        }

        /// <summary>
        /// Fires a signal queued specifically at a target tick.
        /// A current-tick input dispatches synchronously; a non-current input is recorded.
        /// During resimulation nested emissions are recomputed and dispatched once inline,
        /// preserving the outer handler's continuation order. Future emissions wait for
        /// their target tick. A handler cannot emit into an earlier tick.
        /// Submit corrected inputs before beginning rollback. Concurrent external producers must
        /// wait until rollback completes; their calls are rejected rather than silently discarded.
        /// Unlike <see cref="Fire{T}(T)"/>, the current-tick path intentionally dispatches
        /// synchronously on the CALLER thread for deterministic simulation code. Call it only
        /// from the owning/main thread; worker-thread producers must use <see cref="Fire{T}(T)"/>.
        /// </summary>
        public void FireAtTick<T>(T signal, int tick) where T : struct, INetworkSignal
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("FireAtTick must run on the network bus owning thread.");
            bool dispatch;
            NetworkSignalJournal.Entry recorded = default;
            lock (_tickLock)
            {
                if (_isResimulating && _replayDispatchDepth == 0) return;
                if (_currentEventId != 0 && tick < _currentTick)
                    throw new InvalidOperationException("A network handler cannot emit into an earlier tick. Submit late external inputs before rollback.");
                dispatch = tick == _currentTick;
                if (_currentEventId == 0 && Volatile.Read(ref _pendingInputs.Count) != 0)
                    throw new InvalidOperationException("Drain incoming signals before an owner-thread fire.");
                recorded = Record(signal, tick, _currentEventId);
            }

            if (dispatch)
            {
                if (_isResimulating) ReplayEntry(recorded);
                else DispatchLive(signal, recorded.Id);
            }
        }

        // Identity is never an execution-order key. Replay records the corrected tick
        // and within-tick production order, including for newly created future children.
        private NetworkSignalJournal.Entry Record<T>(T signal, int tick, long parentId) where T : struct, INetworkSignal
        {
            long id = _journal.NextId();
            long order = _isResimulating ? ++_recordOrder : id;
            var history = GetOrCreateHistory<T>();
            var recorded = _journal.Add(tick, history.AddTracked(tick, signal, parentId != 0, _currentTick, order),
                history, id, parentId, _currentTick, order);
            if (_isResimulating && tick > _currentTick && tick <= _replayTarget)
                AddGeneratedInput(recorded);
            return recorded;
        }

        private void ReplayEntry(NetworkSignalJournal.Entry entry)
        {
            _recordOrder++;
            _replayDispatchDepth++;
            long previous = _currentEventId;
            _currentEventId = entry.Id;
            try { entry.History.ReplaySignal(entry.Sequence, _localSignalBus); }
            finally { _currentEventId = previous; _replayDispatchDepth--; }
        }

        private void AddGeneratedInput(NetworkSignalJournal.Entry entry)
            => NetworkSignalJournal.InsertScheduled(_generatedInputs, _generatedCursor, entry);

        /// <summary>
        /// Re-simulates all buffered network signals starting from a specific rollback tick up to the target tick.
        /// Clears future external predictions and regenerates events produced by replayed
        /// handlers. Pending schedules from before the rollback tick remain intact.
        ///
        /// Snapshot convention (A2): snapshot[tick] is the model state BEFORE that tick's
        /// signals are applied — the same convention SetTick uses (capture, then fire).
        /// The loop therefore CAPTURES first, then REPLAYS. Capturing after replay would
        /// make snapshot[tick] the post-signal state, which is inconsistent with SetTick
        /// snapshots and causes a subsequent Restore(tick)+Replay(tick) to apply the tick's
        /// signals twice (double-apply). The deterministic-repeat rollback tests guard this.
        /// </summary>
        public void RollbackAndResimulate(int rollbackTick, int targetTick)
        {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Rollback must run on the network bus owning thread.");
            if (rollbackTick > targetTick) throw new ArgumentOutOfRangeException(nameof(targetTick));
            int replayCount;
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("A rollback is already in progress.");
                if (_currentEventId != 0) throw new InvalidOperationException("Cannot start rollback inside a signal handler.");
                if (Volatile.Read(ref _pendingInputs.Count) != 0) throw new InvalidOperationException("Drain incoming network signals on the owner thread before rollback.");
                lock (_modelHandlersLock)
                {
                    for (int i = 0; i < _modelHandlers.Count; i++)
                        if (!_modelHandlers[i].HasSnapshot(rollbackTick))
                            throw new InvalidOperationException(
                                $"Cannot roll back to tick {rollbackTick}: a registered model has no valid snapshot of it. Nothing was changed.");
                    // Snapshots beyond the restored point belong to the abandoned timeline,
                    // including those beyond a partial replay's target. Rebuild only what runs.
                    for (int i = 0; i < _modelHandlers.Count; i++) _modelHandlers[i].InvalidateAfter(rollbackTick);
                }
                for (int i = 0; i < _historyList.Count; i++)
                    _historyList[i].PrepareRollback(rollbackTick, targetTick);
                _journal.PrepareRollback(rollbackTick, targetTick);
                replayCount = _journal.CopyReplayInputs(rollbackTick, targetTick, ref _replayActions, _generatedInputs);
                _replayTarget = targetTick;
                _generatedCursor = 0;
                _isResimulating = true;
            }
            try
            {
                // Restore models to the rollback tick state first
                lock (_modelHandlersLock)
                {
                    for (int i = 0; i < _modelHandlers.Count; i++)
                    {
                        _modelHandlers[i].Restore(rollbackTick);
                    }
                }

                _currentTick = rollbackTick;
                int replayCursor = 0;

                // Replay all signals starting from the rollback point up to the new target tick.
                // Order matters: Capture BEFORE Replay keeps the pre-tick snapshot contract.
                while (_currentTick <= targetTick)
                {
                    _recordOrder = 0;
                    lock (_modelHandlersLock)
                    {
                        for (int i = 0; i < _modelHandlers.Count; i++)
                        {
                            _modelHandlers[i].Capture(_currentTick);
                        }
                    }

                    // Previously scheduled work is due before this tick's external calls.
                    while (_generatedCursor < _generatedInputs.Count && _generatedInputs[_generatedCursor].Tick == _currentTick)
                        ReplayEntry(_generatedInputs[_generatedCursor++]);

                    while (replayCursor < replayCount && _replayActions[replayCursor].ActionTick == _currentTick)
                    {
                        var entry = _replayActions[replayCursor++];
                        lock (_tickLock) entry = _journal.SetRecordOrder(entry, _currentTick, ++_recordOrder);
                        if (entry.Tick == _currentTick) ReplayEntry(entry);
                        else AddGeneratedInput(entry);
                    }
                    if (_currentTick == int.MaxValue) break;
                    _currentTick++;
                }
            }
            finally
            {
                Array.Clear(_replayActions, 0, replayCount);
                _generatedInputs.Clear();
                lock (_tickLock) _isResimulating = false;
            }
        }

        /// <summary>
        /// Prunes the confirmed tick and every tick before it (signals, journal and model snapshots) to bound memory.
        /// Once tick N is confirmed a rollback targets a later tick, so the oldest tick a rollback can reach afterwards
        /// is N + 1; a rollback to a pruned tick throws (#170).
        /// </summary>
        public void PruneHistory(int confirmedTick)
        {
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("Cannot prune history during rollback.");
                _journal.Prune(confirmedTick);
                for (int i = 0; i < _historyList.Count; i++) _historyList[i].Prune(confirmedTick);
                lock (_modelHandlersLock)
                {
                    for (int i = 0; i < _modelHandlers.Count; i++) _modelHandlers[i].Prune(confirmedTick);
                }
            }
        }

        /// <summary>
        /// Clears all signal and model history.
        /// </summary>
        public void Clear()
        {
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("Cannot clear history during rollback.");
                _histories.Clear();
                _historyList.Clear();
                _journal.Clear();
                _queueEpoch++;
                _pendingInputs = new NetworkInputBatch();
                lock (_modelHandlersLock) _modelHandlers.Clear();
            }
        }
    }
}
