using System.Collections.Generic;
using System.Threading;
using Nexus.Core;

namespace Nexus.Netcode
{
    internal sealed class NetworkInputBatch { internal int Count; }

    /// <summary>Private queue transport; only the real payload enters signal middleware.</summary>
    internal sealed class QueuedNetworkInput<T> : IQueuedSignal where T : struct, INetworkSignal
    {
        private static readonly Stack<QueuedNetworkInput<T>> Pool = new();
        private static readonly object PoolLock = new();
        private NetworkSignalBus _owner;
        private NetworkInputBatch _batch;
        private T _signal;
        private long _eventId;
        private int _epoch, _released;

        static QueuedNetworkInput() => QueuedSignalPoolRegistry.Register(ClearPool);
        private static void ClearPool() { lock (PoolLock) Pool.Clear(); }

        internal static QueuedNetworkInput<T> Rent(NetworkSignalBus owner, T signal, long eventId, int epoch, NetworkInputBatch batch)
        {
            QueuedNetworkInput<T> item;
            lock (PoolLock) item = Pool.Count == 0 ? new QueuedNetworkInput<T>() : Pool.Pop();
            item._owner = owner;
            item._batch = batch;
            item._signal = signal;
            item._eventId = eventId;
            item._epoch = epoch;
            item._released = 0;
            return item;
        }

        public void Fire(SignalBus bus) => _owner.DispatchQueued(_signal, _eventId, _epoch);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            // Queue.Clear holds its queue lock. Release must never acquire the owner's
            // tick lock, which producers hold while enqueueing (opposite lock order).
            Interlocked.Decrement(ref _batch.Count);
            _batch = null;
            _owner = null;
            _signal = default;
            _eventId = 0;
            lock (PoolLock) { if (Pool.Count < 256) Pool.Push(this); }
        }
    }
}
