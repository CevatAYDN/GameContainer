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
    /// Buffered tick-based event wrapper for deterministic replication and rollback simulation.
    /// </summary>
    public struct BufferedNetworkSignal
    {
        public int Tick { get; }
        public object Signal { get; }

        public BufferedNetworkSignal(int tick, object signal)
        {
            Tick = tick;
            Signal = signal;
        }
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
            var keys = new List<int>(_snapshots.Keys);
            foreach (var k in keys)
            {
                if (k < tick)
                {
                    _snapshots.Remove(k);
                }
            }
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
        private readonly List<BufferedNetworkSignal> _signalHistory = new();
        private readonly List<INetworkModelSnapshotHandler> _modelHandlers = new();
        private int _currentTick;

        public int CurrentTick => _currentTick;
        public IReadOnlyList<BufferedNetworkSignal> SignalHistory => _signalHistory;

        public NetworkSignalBus(ISignalBus localSignalBus)
        {
            _localSignalBus = localSignalBus;
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
            _signalHistory.Add(new BufferedNetworkSignal(_currentTick, signal));
            _localSignalBus.Fire(signal);
        }

        /// <summary>
        /// Fires a signal queued specifically at a target tick.
        /// </summary>
        public void FireAtTick<T>(T signal, int tick) where T : struct, INetworkSignal
        {
            _signalHistory.Add(new BufferedNetworkSignal(tick, signal));
            
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
            _signalHistory.RemoveAll(s => s.Tick > targetTick);

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

                for (int i = 0; i < _signalHistory.Count; i++)
                {
                    var buffered = _signalHistory[i];
                    if (buffered.Tick == _currentTick)
                    {
                        if (CustomDispatcher != null)
                        {
                            CustomDispatcher(_localSignalBus, buffered.Signal);
                        }
                        else
                        {
                            // Reflect fire method fallback
                            var signalType = buffered.Signal.GetType();
                            var fireMethod = typeof(ISignalBus).GetMethod("Fire").MakeGenericMethod(signalType);
                            fireMethod.Invoke(_localSignalBus, new[] { buffered.Signal });
                        }
                    }
                }
                _currentTick++;
            }
        }

        /// <summary>
        /// Prunes history older than a confirmed checkpoint tick to prevent memory leaks.
        /// </summary>
        public void PruneHistory(int confirmedTick)
        {
            _signalHistory.RemoveAll(s => s.Tick < confirmedTick);
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
            _signalHistory.Clear();
            _modelHandlers.Clear();
        }
    }
}
