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
        void Restore(int tick);
        void Prune(int confirmedTick);
    }

    /// <summary>
    /// Generic implementation wrapper for snapshot handlers.
    /// </summary>
    public class NetworkModelSnapshotHandler<TState> : INetworkModelSnapshotHandler where TState : struct
    {
        private readonly ISnapshotableModel<TState> _model;
        private readonly Dictionary<int, TState> _snapshots = new();
        // Reusable list to avoid allocating a new List per Prune call (0-GC steady state).
        private readonly List<int> _keysToPrune = new();

        public NetworkModelSnapshotHandler(ISnapshotableModel<TState> model)
        {
            _model = model;
        }

        public void Capture(int tick)
        {
            _snapshots[tick] = _model.CaptureSnapshot();
        }

        public void Restore(int tick)
        {
            if (_snapshots.TryGetValue(tick, out var state))
            {
                _model.RestoreSnapshot(state);
            }
        }

        public void Prune(int confirmedTick)
        {
            _keysToPrune.Clear();
            foreach (var kvp in _snapshots)
            {
                if (kvp.Key <= confirmedTick) _keysToPrune.Add(kvp.Key);
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
    }

    public class NetworkSignalHistory<T> : INetworkSignalHistory, IOrderedSignalHistory where T : struct, INetworkSignal
    {
        private readonly List<BufferedNetworkSignal<T>> _signals;
        private readonly object _signalsLock = new();
        private BufferedNetworkSignal<T>[] _replayBuffer;
        private readonly object _replayLock = new();
        private long _nextSequence;

        // Return a stable snapshot for readers.  Add/rollback/prune are synchronized below;
        // exposing List<T>.AsReadOnly() directly would still let an external enumerator race
        // with in-place compaction.  This property is an inspection API, not the Fire hot path.
        public IReadOnlyList<BufferedNetworkSignal<T>> Signals
        {
            get
            {
                lock (_signalsLock)
                {
                    return new System.Collections.ObjectModel.ReadOnlyCollection<BufferedNetworkSignal<T>>(
                        _signals.ToArray());
                }
            }
        }

        public NetworkSignalHistory(int initialCapacity = 256)
        {
            int capacity = Math.Max(1, initialCapacity);
            _signals = new List<BufferedNetworkSignal<T>>(capacity);
            _replayBuffer = new BufferedNetworkSignal<T>[capacity];
        }

        public void Add(int tick, T signal)
            => AddTracked(tick, signal);

        internal long AddTracked(int tick, T signal)
        {
            lock (_signalsLock)
            {
                long sequence = ++_nextSequence;
                _signals.Add(new BufferedNetworkSignal<T> { Tick = tick, Signal = signal, Sequence = sequence });
                return sequence;
            }
        }

        void IOrderedSignalHistory.ReplaySignal(long sequence, ISignalBus bus)
        {
            if (TryReadSignal(sequence, out var signal)) DispatchReplay(signal, bus);
        }

        private bool TryReadSignal(long sequence, out T signal)
        {
            lock (_signalsLock)
            {
                int low = 0, high = _signals.Count - 1;
                while (low <= high)
                {
                    int mid = low + ((high - low) >> 1);
                    long candidate = _signals[mid].Sequence;
                    if (candidate < sequence) low = mid + 1;
                    else if (candidate > sequence) high = mid - 1;
                    else
                    {
                        signal = _signals[mid].Signal;
                        return true;
                    }
                }
                signal = default;
                return false; // pruned while a replay snapshot was in flight
            }
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
            // Reuse a private replay buffer so rollback remains allocation-free after the
            // history's initial capacity is reached.  The replay lock serializes concurrent
            // rollback callers; user handlers run after the history lock is released and may
            // safely append a new signal for a later tick.
            lock (_replayLock)
            {
                int signalCount;
                lock (_signalsLock)
                {
                    signalCount = _signals.Count;
                    if (_replayBuffer.Length < signalCount)
                    {
                        Array.Resize(ref _replayBuffer, Math.Max(signalCount, _replayBuffer.Length * 2));
                    }
                    _signals.CopyTo(_replayBuffer, 0);
                }

                // The `is SignalBus` pattern check ran PER SIGNAL inside the loop
                // (O(N) cast checks per replay). Hoisted out — one cast per replay call.
                var concreteBus = localSignalBus as SignalBus;
                for (int i = 0; i < signalCount; i++)
                {
                    if (_replayBuffer[i].Tick == tick)
                    {
                        try
                        {
                            // Synchronous inline dispatch: rollback resimulation captures a
                            // model snapshot per tick, so this tick's signals must be fully
                            // applied before the loop advances.
                            localSignalBus.Fire(_replayBuffer[i].Signal);
                        }
                        catch (NexusSyncAsyncMismatchException)
                        {
                            NexusRuntime.Logger?.LogError(
                                $"[NetworkSignalBus] Signal '{typeof(T).FullName}' has async handlers — synchronous deterministic replay is impossible. " +
                                "Rollback snapshots captured for this tick will not include this signal's effects. " +
                                "Use sync-only handlers for networked signals that participate in rollback.");
                            // Best-effort delivery so the signal is not silently dropped.
                            if (concreteBus != null)
                            {
                                concreteBus.FireQueued(_replayBuffer[i].Signal);
                            }
                        }
                    }
                }
            }
        }

        public void RemoveSignalsAfter(int tick)
        {
            // In-place compaction: single O(N) pass, zero allocation. Repeated RemoveAt
            // in the old backwards loop was O(N²) for large histories (each removal
            // shifts every later element). List.RemoveAll would allocate a predicate;
            // manual compaction keeps the 0-GC steady-state guarantee.
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
            // Same O(N), zero-allocation in-place compaction as RemoveSignalsAfter —
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
        private readonly List<INetworkSignalHistory> _historyList = new(); // owned by _tickLock
        private System.Collections.ObjectModel.ReadOnlyDictionary<Type, INetworkSignalHistory> _historiesReadOnly;
        private readonly List<INetworkModelSnapshotHandler> _modelHandlers = new();
        // _modelHandlers is registered from setup code but iterated from tick/rollback paths;
        // guarded by a lock to match the concurrent design of _histories.
        private readonly object _modelHandlersLock = new();
        private volatile int _currentTick;

        // Replay consumes the original journal, including inline nested dispatches.
        private volatile bool _isResimulating;
        private readonly int _ownerThreadId;
        private readonly object _tickLock = new();
        private readonly NetworkSignalJournal _journal = new();
        private NetworkSignalJournal.Entry[] _orderedReplayBuffer;
        private int _replayCursor, _replayCount, _replayDispatchDepth;
        private long _currentEventId;
        private int _queueEpoch;
        private NetworkInputBatch _pendingInputs = new();
        private readonly Dictionary<long, int> _nestedReplayHeads = new();
        private int[] _nextNestedReplay;
        private bool _generatingReplay;
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
            // All writers hold _tickLock. Keep a list for allocation-free lifecycle
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
            lock (_modelHandlersLock)
            {
                _modelHandlers.Add(new NetworkModelSnapshotHandler<TState>(model));
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
            NetworkSignalJournal.Entry nested = default;
            NetworkSignalJournal.Entry recorded = default;
            bool owner = Thread.CurrentThread.ManagedThreadId == _ownerThreadId;
            int epoch = 0;
            lock (_tickLock)
            {
                if (_isResimulating && !owner)
                    throw new InvalidOperationException("Submit incoming network signals before rollback or after it completes.");
                if (_isResimulating && !_generatingReplay)
                {
                    if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                        throw new InvalidOperationException("Submit incoming network signals before rollback or after it completes.");
                    if (_replayDispatchDepth == 0) return; // snapshot restoration, not a replayed handler
                    nested = ConsumeNestedReplay<T>(_currentTick);
                }
                else
                {
                    if (!owner && !(_localSignalBus is SignalBus))
                        throw new NotSupportedException("Worker network inputs require Nexus SignalBus. Use the owner thread with a custom signal bus.");
                    if (owner && _currentEventId == 0 && Volatile.Read(ref _pendingInputs.Count) != 0)
                        throw new InvalidOperationException("Drain incoming signals before an owner-thread fire.");
                    var history = GetOrCreateHistory<T>();
                    recorded = _journal.Add(_currentTick, history.AddTracked(_currentTick, signal), history, owner ? _currentEventId : 0);
                    if (!owner)
                    {
                        epoch = _queueEpoch;
                        Interlocked.Increment(ref _pendingInputs.Count);
                        // Record and enqueue share one ordering boundary across producers.
                        ((SignalBus)_localSignalBus).EnqueueDispatch(QueuedNetworkInput<T>.Rent(this, signal, recorded.Id, epoch, _pendingInputs));
                    }
                }
            }
            if (nested.History != null) { ReplayEntry(nested); return; }

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
            lock (_tickLock) _journal.MarkApplied(eventId);
            try
            {
                // Preserve the allocation-free owner-thread fast path.  Async handlers still use
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
        /// The synchronous local fire only happens when the target tick equals the
        /// current tick AND the bus is NOT mid-resimulation. During RollbackAndResimulate
        /// the tick pointer moves as signals replay, so firing here would double-apply a
        /// signal to the models (once from replay, once from this call). Inside a
        /// resimulation current-tick nested emissions consume their original journal entry
        /// inline, preserving the outer handler's continuation order. Future-tick emissions
        /// are already in the journal and wait for that tick.
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
            NetworkSignalJournal.Entry nested = default;
            NetworkSignalJournal.Entry recorded = default;
            long eventId = 0;
            lock (_tickLock)
            {
                if (_isResimulating && !_generatingReplay)
                {
                    if (_replayDispatchDepth == 0) return;
                    if (tick != _currentTick)
                    {
                        // Entries beyond the target were invalidated before replay. Restore
                        // scheduling there so a partial rollback does not lose future work.
                        if (tick > _replayTarget)
                        {
                            var future = GetOrCreateHistory<T>();
                            _journal.Add(tick, future.AddTracked(tick, signal), future);
                        }
                        return;
                    }
                    nested = ConsumeNestedReplay<T>(tick);
                    dispatch = false;
                }
                else
                {
                    var history = GetOrCreateHistory<T>();
                    dispatch = tick == _currentTick;
                    if (dispatch && _currentEventId == 0 && Volatile.Read(ref _pendingInputs.Count) != 0)
                        throw new InvalidOperationException("Drain incoming signals before an owner-thread fire.");
                    recorded = _journal.Add(tick, history.AddTracked(tick, signal), history, dispatch ? _currentEventId : 0);
                    eventId = recorded.Id;
                    if (_isResimulating && !dispatch && tick > _currentTick && tick <= _replayTarget)
                        AddGeneratedInput(recorded);
                }
            }
            if (nested.History != null) { ReplayEntry(nested); return; }

            if (dispatch)
            {
                if (_isResimulating) ReplayEntry(recorded);
                else DispatchLive(signal, eventId);
            }
        }

        private NetworkSignalJournal.Entry ConsumeNestedReplay<T>(int tick) where T : struct, INetworkSignal
        {
            // Indexed by original parent identity, so interleaved external inputs of the
            // same type cannot be mistaken for this handler's nested emission.
            if (_nestedReplayHeads.TryGetValue(_currentEventId, out int i) && i >= 0)
            {
                var entry = _orderedReplayBuffer[i];
                if (entry.Tick == tick && entry.History is NetworkSignalHistory<T>)
                {
                    _nestedReplayHeads[_currentEventId] = _nextNestedReplay[i];
                    _orderedReplayBuffer[i] = default;
                    return entry;
                }
            }
            throw new InvalidOperationException($"Replay emitted unrecorded nested signal '{typeof(T).FullName}' at tick {tick}.");
        }

        private void ReplayEntry(NetworkSignalJournal.Entry entry)
        {
            _replayDispatchDepth++;
            long previous = _currentEventId;
            bool wasGenerating = _generatingReplay;
            lock (_tickLock)
            {
                _generatingReplay = !_journal.WasApplied(entry.Id);
                _journal.MarkApplied(entry.Id);
            }
            _currentEventId = entry.Id;
            try { entry.History.ReplaySignal(entry.Sequence, _localSignalBus); }
            finally { _currentEventId = previous; _generatingReplay = wasGenerating; _replayDispatchDepth--; }
        }

        private void AddGeneratedInput(NetworkSignalJournal.Entry entry)
        {
            int low = _generatedCursor, high = _generatedInputs.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (_generatedInputs[mid].Tick <= entry.Tick) low = mid + 1;
                else high = mid;
            }
            _generatedInputs.Insert(low, entry);
        }

        /// <summary>
        /// Re-simulates all buffered network signals starting from a specific rollback tick up to the target tick.
        /// Clears invalid future signals during rollback.
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
                for (int i = 0; i < _historyList.Count; i++) _historyList[i].RemoveSignalsAfter(targetTick);
                _journal.RemoveAfter(targetTick);
                replayCount = _journal.CopyRange(rollbackTick, targetTick, ref _orderedReplayBuffer);
                _replayCursor = 0;
                _replayCount = replayCount;
                _replayTarget = targetTick;
                _generatedCursor = 0;
                _generatedInputs.Clear();
                if (_nextNestedReplay == null || _nextNestedReplay.Length < replayCount)
                    _nextNestedReplay = new int[Math.Max(256, replayCount * 2)];
                _nestedReplayHeads.Clear();
                for (int i = replayCount - 1; i >= 0; i--)
                {
                    long parentId = _orderedReplayBuffer[i].ParentId;
                    if (parentId == 0) continue;
                    _nextNestedReplay[i] = _nestedReplayHeads.TryGetValue(parentId, out int head) ? head : -1;
                    _nestedReplayHeads[parentId] = i;
                }
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

                // Replay all signals starting from the rollback point up to the new target tick.
                // Order matters: Capture BEFORE Replay keeps the pre-tick snapshot contract.
                while (_currentTick <= targetTick)
                {
                    lock (_modelHandlersLock)
                    {
                        for (int i = 0; i < _modelHandlers.Count; i++)
                        {
                            _modelHandlers[i].Capture(_currentTick);
                        }
                    }

                    while (_replayCursor < replayCount)
                    {
                        var entry = _orderedReplayBuffer[_replayCursor];
                        if (entry.History == null) { _replayCursor++; continue; }
                        if (entry.Tick != _currentTick) break;
                        if (entry.ParentId != 0)
                            throw new InvalidOperationException("Replay handler did not emit its recorded nested signal.");
                        _orderedReplayBuffer[_replayCursor++] = default;
                        ReplayEntry(entry);
                    }
                    while (_generatedCursor < _generatedInputs.Count && _generatedInputs[_generatedCursor].Tick == _currentTick)
                        ReplayEntry(_generatedInputs[_generatedCursor++]);
                    if (_currentTick == int.MaxValue) break;
                    _currentTick++;
                }
            }
            finally
            {
                Array.Clear(_orderedReplayBuffer, 0, replayCount);
                _generatedInputs.Clear();
                lock (_tickLock) { _isResimulating = false; _replayCount = _replayCursor = 0; _nestedReplayHeads.Clear(); }
            }
        }

        /// <summary>
        /// Prunes history older than a confirmed checkpoint tick to prevent memory leaks.
        /// </summary>
        public void PruneHistory(int confirmedTick)
        {
            lock (_tickLock)
            {
                if (_isResimulating) throw new InvalidOperationException("Cannot prune history during rollback.");
                _journal.Prune(confirmedTick);
                for (int i = 0; i < _historyList.Count; i++) _historyList[i].Prune(confirmedTick);
            }
            lock (_modelHandlersLock)
            {
                for (int i = 0; i < _modelHandlers.Count; i++)
                {
                    _modelHandlers[i].Prune(confirmedTick);
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
            }
            lock (_modelHandlersLock)
            {
                _modelHandlers.Clear();
            }
        }
    }
}
