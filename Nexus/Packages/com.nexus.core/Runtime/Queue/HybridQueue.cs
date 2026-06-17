using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus.Core
{
    public interface IQueuedSignalDrainer
    {
        void Drain(SignalBus bus);
    }

    public class TypedQueueContainer<T> : IQueuedSignalDrainer where T : struct
    {
        public readonly ConcurrentQueue<T> Queue = new();

        public void Drain(SignalBus bus)
        {
            while (Queue.TryDequeue(out var signal))
            {
                bus.Fire(signal);
            }
        }
    }

    public class HybridQueue
    {
        private readonly SignalBus _signalBus;
        
        private readonly Dictionary<Type, IQueuedSignalDrainer> _threadSafeQueues = new();
        private readonly List<IQueuedSignalDrainer> _activeThreadSafeQueues = new();
        private readonly object _lock = new();

        private readonly Dictionary<Type, IQueuedSignalDrainer> _nextFrameQueues = new();
        private readonly List<IQueuedSignalDrainer> _activeNextFrameQueues = new();

        public HybridQueue(SignalBus signalBus)
        {
            _signalBus = signalBus;
        }

        public void EnqueueThreadSafe<T>(T signal) where T : struct
        {
            var drainer = GetOrCreateThreadSafeQueue<T>();
            ((TypedQueueContainer<T>)drainer).Queue.Enqueue(signal);
        }

        public void EnqueueNextFrame<T>(T signal) where T : struct
        {
            var drainer = GetOrCreateNextFrameQueue<T>();
            ((TypedQueueContainer<T>)drainer).Queue.Enqueue(signal);
        }

        private IQueuedSignalDrainer GetOrCreateThreadSafeQueue<T>() where T : struct
        {
            lock (_lock)
            {
                var type = typeof(T);
                if (!_threadSafeQueues.TryGetValue(type, out var queue))
                {
                    queue = new TypedQueueContainer<T>();
                    _threadSafeQueues[type] = queue;
                    _activeThreadSafeQueues.Add(queue);
                }
                return queue;
            }
        }

        private IQueuedSignalDrainer GetOrCreateNextFrameQueue<T>() where T : struct
        {
            var type = typeof(T);
            if (!_nextFrameQueues.TryGetValue(type, out var queue))
            {
                queue = new TypedQueueContainer<T>();
                _nextFrameQueues[type] = queue;
                _activeNextFrameQueues.Add(queue);
            }
            return queue;
        }

        public void DrainThreadSafe()
        {
            // Drain thread-safe queue (typically called in Update)
            // Using index-based for loop to avoid any list enumerator allocation
            for (int i = 0; i < _activeThreadSafeQueues.Count; i++)
            {
                _activeThreadSafeQueues[i].Drain(_signalBus);
            }
        }

        public void DrainNextFrame()
        {
            // Drain next-frame queue (typically called in LateUpdate)
            for (int i = 0; i < _activeNextFrameQueues.Count; i++)
            {
                _activeNextFrameQueues[i].Drain(_signalBus);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _threadSafeQueues.Clear();
                _activeThreadSafeQueues.Clear();
                _nextFrameQueues.Clear();
                _activeNextFrameQueues.Clear();
            }
        }
    }
}
