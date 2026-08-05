using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    }

    public class NetworkSignalHistory<T> : INetworkSignalHistory where T : struct, INetworkSignal
    {
        private readonly List<BufferedNetworkSignal<T>> _signals;
        private System.Collections.ObjectModel.ReadOnlyCollection<BufferedNetworkSignal<T>> _signalsReadOnly;

        // Read-only live view — external callers must not mutate the history directly.
        public IReadOnlyList<BufferedNetworkSignal<T>> Signals => _signalsReadOnly ??= _signals.AsReadOnly();

        public NetworkSignalHistory(int initialCapacity = 256)
        {
            _signals = new List<BufferedNetworkSignal<T>>(initialCapacity);
        }

        public void Add(int tick, T signal)
        {
            _signals.Add(new BufferedNetworkSignal<T> { Tick = tick, Signal = signal });
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
            // The `is SignalBus` pattern check ran PER SIGNAL inside the loop
            // (O(N) cast checks per replay). Hoisted out — one cast per replay call.
            var concreteBus = localSignalBus as SignalBus;
            for (int i = 0; i < _signals.Count; i++)
            {
                if (_signals[i].Tick == tick)
                {
                    try
                    {
                        // Synchronous inline dispatch: rollback resimulation captures a
                        // model snapshot per tick, so this tick's signals must be fully
                        // applied before the loop advances. FireQueued's fire-and-forget
                        // async path would defer application and every snapshot would
                        // observe the same un-resimulated state.
                        localSignalBus.Fire(_signals[i].Signal);
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
                            concreteBus.FireQueued(_signals[i].Signal);
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

        public void Prune(int confirmedTick)
        {
            // Same O(N), zero-allocation in-place compaction as RemoveSignalsAfter —
            // keeps only signals strictly newer than the confirmed tick.
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

        public void Clear()
        {
            _signals.Clear();
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
        private System.Collections.ObjectModel.ReadOnlyDictionary<Type, INetworkSignalHistory> _historiesReadOnly;
        private readonly List<INetworkModelSnapshotHandler> _modelHandlers = new();
        // _modelHandlers is registered from setup code but iterated from tick/rollback paths;
        // guarded by a lock to match the concurrent design of _histories.
        private readonly object _modelHandlersLock = new();
        private volatile int _currentTick;

        // True while RollbackAndResimulate is driving the tick pointer. FireAtTick
        // records to history but suppresses the synchronous local fire during this
        // window so a signal cannot be applied twice (once by replay, once by the call).
        private volatile bool _isResimulating;

        public int CurrentTick => _currentTick;
        // Read-only live wrapper — prevents callers from casting back to the mutable dictionary.
        public IReadOnlyDictionary<Type, INetworkSignalHistory> Histories =>
            _historiesReadOnly ??= new System.Collections.ObjectModel.ReadOnlyDictionary<Type, INetworkSignalHistory>(_histories);

        public NetworkSignalBus(ISignalBus localSignalBus)
        {
            _localSignalBus = localSignalBus;
        }

        private NetworkSignalHistory<T> GetOrCreateHistory<T>() where T : struct, INetworkSignal
        {
            var type = typeof(T);
            // GetOrAdd is atomic — two concurrent Fire<T> calls for a new signal
            // type can never both create and publish a history, and the returned instance
            // is always the single published one.
            return (NetworkSignalHistory<T>)_histories.GetOrAdd(type, static _ => new NetworkSignalHistory<T>());
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
            _currentTick = tick;
            // Capture state of all registered models for the new tick
            lock (_modelHandlersLock)
            {
                for (int i = 0; i < _modelHandlers.Count; i++)
                {
                    _modelHandlers[i].Capture(_currentTick);
                }
            }
        }

        /// <summary>
        /// Fires a signal immediately and registers it in the tick history.
        /// Uses FireQueued to avoid NexusSyncAsyncMismatchException when the signal
        /// has async handlers registered on the local bus.
        /// </summary>
        public void Fire<T>(T signal) where T : struct, INetworkSignal
        {
            GetOrCreateHistory<T>().Add(_currentTick, signal);
            // Always route through FireQueued: if the signal has async handlers,
            // Fire() would throw NexusSyncAsyncMismatchException. FireQueued is
            // async-safe and guarantees delivery on the next main-thread drain.
            if (_localSignalBus is SignalBus concreteBus)
                concreteBus.FireQueued(signal);
            else
                _localSignalBus.Fire(signal);
        }

    /// <summary>
    /// Fires a signal queued specifically at a target tick.
    /// The synchronous local fire only happens when the target tick equals the
    /// current tick AND the bus is NOT mid-resimulation. During RollbackAndResimulate
    /// the tick pointer moves as signals replay, so firing here would double-apply a
    /// signal to the models (once from replay, once from this call). Inside a
    /// resimulation the signal is recorded to history only; the replay loop applies it.
    /// </summary>
    public void FireAtTick<T>(T signal, int tick) where T : struct, INetworkSignal
    {
        GetOrCreateHistory<T>().Add(tick, signal);
        
        if (tick == _currentTick && !_isResimulating)
        {
            // Route through FireQueued exactly like Fire() so a signal with async
            // handlers/subscriptions on the local bus never throws
            // NexusSyncAsyncMismatchException (which would abort the caller's tick loop).
            if (_localSignalBus is SignalBus concreteBus)
                concreteBus.FireQueued(signal);
            else
                _localSignalBus.Fire(signal);
        }
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
            _isResimulating = true;
            try
            {
                // Prune signals that occurred after the target tick (future prediction mistakes)
                foreach (var history in _histories.Values)
                {
                    history.RemoveSignalsAfter(targetTick);
                }

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

                    foreach (var history in _histories.Values)
                    {
                        history.ReplaySignals(_currentTick, _localSignalBus);
                    }
                    _currentTick++;
                }
            }
            finally
            {
                _isResimulating = false;
            }
        }

        /// <summary>
        /// Prunes history older than a confirmed checkpoint tick to prevent memory leaks.
        /// </summary>
        public void PruneHistory(int confirmedTick)
        {
            foreach (var history in _histories.Values)
            {
                history.Prune(confirmedTick);
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
            _histories.Clear();
            lock (_modelHandlersLock)
            {
                _modelHandlers.Clear();
            }
        }
    }
}
