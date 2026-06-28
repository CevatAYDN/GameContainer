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
        void Prune(int tick);
    }

    /// <summary>
    /// Generic implementation wrapper for snapshot handlers.
    /// </summary>
    public class NetworkModelSnapshotHandler<TState> : INetworkModelSnapshotHandler where TState : struct
    {
        private readonly ISnapshotableModel<TState> _model;
        private readonly Dictionary<int, TState> _snapshots = new();

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

        public void Prune(int tick)
        {
            // Backwards index loop to prune without allocations (0-GC)
            var keys = new List<int>(_snapshots.Keys);
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                int k = keys[i];
                if (k < tick)
                {
                    _snapshots.Remove(k);
                }
            }
        }
    }

    public interface INetworkSignalHistory
    {
        void ReplaySignals(int tick, ISignalBus localSignalBus);
        void RemoveSignalsAfter(int tick);
        void Prune(int tick);
        void Clear();
    }

    public struct BufferedNetworkSignal<T> where T : struct
    {
        public int Tick;
        public T Signal;
    }

    public class NetworkSignalHistory<T> : INetworkSignalHistory where T : struct, INetworkSignal
    {
        private readonly List<BufferedNetworkSignal<T>> _signals = new();

        public List<BufferedNetworkSignal<T>> Signals => _signals;

        public void Add(int tick, T signal)
        {
            _signals.Add(new BufferedNetworkSignal<T> { Tick = tick, Signal = signal });
        }

        public void ReplaySignals(int tick, ISignalBus localSignalBus)
        {
            for (int i = 0; i < _signals.Count; i++)
            {
                if (_signals[i].Tick == tick)
                {
                    localSignalBus.Fire(_signals[i].Signal);
                }
            }
        }

        public void RemoveSignalsAfter(int tick)
        {
            // Backwards index loop to prune without allocations (0-GC)
            for (int i = _signals.Count - 1; i >= 0; i--)
            {
                if (_signals[i].Tick > tick)
                {
                    _signals.RemoveAt(i);
                }
            }
        }

        public void Prune(int tick)
        {
            // Backwards index loop to prune older history
            for (int i = _signals.Count - 1; i >= 0; i--)
            {
                if (_signals[i].Tick < tick)
                {
                    _signals.RemoveAt(i);
                }
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
        /// <summary>
        /// Code-generated dispatcher delegate to bypass reflection during rollback simulations.
        /// </summary>
        public static Action<ISignalBus, object> CustomDispatcher;

        private readonly ISignalBus _localSignalBus;
        private readonly Dictionary<Type, INetworkSignalHistory> _histories = new();
        private readonly List<INetworkModelSnapshotHandler> _modelHandlers = new();
        private int _currentTick;

        public int CurrentTick => _currentTick;
        public Dictionary<Type, INetworkSignalHistory> Histories => _histories;

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
        /// </summary>
        public void Fire<T>(T signal) where T : struct, INetworkSignal
        {
            GetOrCreateHistory<T>().Add(_currentTick, signal);
            _localSignalBus.Fire(signal);
        }

        /// <summary>
        /// Fires a signal queued specifically at a target tick.
        /// </summary>
        public void FireAtTick<T>(T signal, int tick) where T : struct, INetworkSignal
        {
            GetOrCreateHistory<T>().Add(tick, signal);
            
            if (tick == _currentTick)
            {
                _localSignalBus.Fire(signal);
            }
        }

        /// <summary>
        /// Re-simulates all buffered network signals starting from a specific rollback tick up to the target tick.
        /// Clears invalid future signals during rollback.
        /// </summary>
        public void RollbackAndResimulate(int rollbackTick, int targetTick)
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

            // Replay all signals starting from the rollback point up to the new target tick
            while (_currentTick <= targetTick)
            {
                // Capture snapshots during resimulation steps to update intermediate states
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
