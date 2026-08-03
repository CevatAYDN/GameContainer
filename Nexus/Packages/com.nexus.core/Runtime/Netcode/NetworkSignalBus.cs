using System;
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

        public List<BufferedNetworkSignal<T>> Signals => _signals;

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
        /// B8 contract: deterministic replay ordering is guaranteed for sync handlers
        /// (dispatched in-record-order). Signals with async handlers go through
        /// <see cref="SignalBus.FireQueued"/> which is fire-and-forget — for strict
        /// replay ordering, prefer sync commands or await the dispatch from the caller.
        /// </summary>
        public void ReplaySignals(int tick, ISignalBus localSignalBus)
        {
            for (int i = 0; i < _signals.Count; i++)
            {
                if (_signals[i].Tick == tick)
                {
                    // P0-4 fix: async-aware dispatch — replayed signals with async
                    // handlers no longer throw NexusSyncAsyncMismatchException.
                    if (localSignalBus is SignalBus concreteBus)
                    {
                        concreteBus.FireQueued(_signals[i].Signal);
                    }
                    else
                    {
                        localSignalBus.Fire(_signals[i].Signal);
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
        private readonly Dictionary<Type, INetworkSignalHistory> _histories = new();
        private readonly List<INetworkModelSnapshotHandler> _modelHandlers = new();
        private int _currentTick;

        // B7: true while RollbackAndResimulate is driving the tick pointer. FireAtTick
        // records to history but suppresses the synchronous local fire during this
        // window so a signal cannot be applied twice (once by replay, once by the call).
        private bool _isResimulating;

        public int CurrentTick => _currentTick;
        public IReadOnlyDictionary<Type, INetworkSignalHistory> Histories => _histories;

        public NetworkSignalBus(ISignalBus localSignalBus)
        {
            _localSignalBus = localSignalBus;
        }

        private NetworkSignalHistory<T> GetOrCreateHistory<T>() where T : struct, INetworkSignal
        {
            var type = typeof(T);
            if (!_histories.TryGetValue(type, out var history))
            {
                history = new NetworkSignalHistory<T>();
                _histories[type] = history;
            }
            return (NetworkSignalHistory<T>)history;
        }

        /// <summary>
        /// Registers a snapshotable model to be tracked for rollback states.
        /// </summary>
        public void RegisterModel<TState>(ISnapshotableModel<TState> model) where TState : struct
        {
            _modelHandlers.Add(new NetworkModelSnapshotHandler<TState>(model));
        }

        /// <summary>
        /// Updates the current simulation tick.
        /// </summary>
        public void SetTick(int tick)
        {
            _currentTick = tick;
            // Capture state of all registered models for the new tick
            for (int i = 0; i < _modelHandlers.Count; i++)
            {
                _modelHandlers[i].Capture(_currentTick);
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
    /// B7: the synchronous local fire only happens when the target tick equals the
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
                for (int i = 0; i < _modelHandlers.Count; i++)
                {
                    _modelHandlers[i].Restore(rollbackTick);
                }

                _currentTick = rollbackTick;

                // Replay all signals starting from the rollback point up to the new target tick.
                // Order matters: Capture BEFORE Replay keeps the pre-tick snapshot contract.
                while (_currentTick <= targetTick)
                {
                    for (int i = 0; i < _modelHandlers.Count; i++)
                    {
                        _modelHandlers[i].Capture(_currentTick);
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
            for (int i = 0; i < _modelHandlers.Count; i++)
            {
                _modelHandlers[i].Prune(confirmedTick);
            }
        }

        /// <summary>
        /// Clears all signal and model history.
        /// </summary>
        public void Clear()
        {
            _histories.Clear();
            _modelHandlers.Clear();
        }
    }
}
