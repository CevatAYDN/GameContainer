using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Interface for draining a queue of signals into a <see cref="SignalBus"/>.</summary>
    public interface IQueuedSignalDrainer
    {
        /// <summary>Drains all queued signals into the given signal bus.</summary>
        /// <param name="bus">The target signal bus.</param>
        void Drain(SignalBus bus);
    }

    /// <summary>Thread-safe queue container for a specific signal type.</summary>
    /// <typeparam name="T">The signal struct type.</typeparam>
    public class TypedQueueContainer<T> : IQueuedSignalDrainer where T : struct
    {
        /// <summary>The underlying thread-safe queue.</summary>
        public readonly ConcurrentQueue<T> Queue = new();

        /// <summary>Dequeues all signals and fires them into the given bus.</summary>
        /// <param name="bus">The target signal bus.</param>
        public void Drain(SignalBus bus)
        {
            while (Queue.TryDequeue(out var signal))
            {
                bus.Fire(signal);
            }
        }
    }

    /// <summary>
    /// Manages thread-safe and next-frame deferred signal queues.
    /// Provides zero-allocation draining via reusable scratch lists (Plan §6.1).
    /// Used by <see cref="Context"/> for cross-thread and deferred signal delivery.
    /// </summary>
    [Preserve]
    public class HybridQueue
    {
        private readonly SignalBus _signalBus;
        
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IQueuedSignalDrainer> _threadSafeQueues = new();
        private readonly List<IQueuedSignalDrainer> _activeThreadSafeQueues = new();
        private readonly object _lock = new();

        // Reusable scratch lists to avoid GC allocation every frame (Plan §6.1 zero-alloc steady-state)
        private readonly List<IQueuedSignalDrainer> _drainThreadSafeScratch = new();
        private readonly List<IQueuedSignalDrainer> _drainNextFrameScratch = new();

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IQueuedSignalDrainer> _nextFrameQueues = new();
        private readonly List<IQueuedSignalDrainer> _activeNextFrameQueues = new();

        /// <summary>Creates a new <see cref="HybridQueue"/> backed by the given signal bus.</summary>
        /// <param name="signalBus">The signal bus to drain signals into.</param>
        public HybridQueue(SignalBus signalBus)
        {
            _signalBus = signalBus;
        }

        /// <summary>Enqueues a signal for thread-safe draining on the main thread.</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void EnqueueThreadSafe<T>(T signal) where T : struct
        {
            var type = typeof(T);
            if (!_threadSafeQueues.TryGetValue(type, out var queue))
            {
                queue = GetOrCreateThreadSafeQueue<T>();
            }
            ((TypedQueueContainer<T>)queue).Queue.Enqueue(signal);
        }

        /// <summary>Enqueues a signal to be fired at the start of the next frame (LateUpdate drain).</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void EnqueueNextFrame<T>(T signal) where T : struct
        {
            var type = typeof(T);
            if (!_nextFrameQueues.TryGetValue(type, out var queue))
            {
                queue = GetOrCreateNextFrameQueue<T>();
            }
            ((TypedQueueContainer<T>)queue).Queue.Enqueue(signal);
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
            lock (_lock)
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
        }

        /// <summary>
        /// Drains all thread-safe queued signals into the signal bus.
        /// Called from <c>Root.Update()</c>. Zero-allocation (uses reusable scratch list).
        /// </summary>
        public void DrainThreadSafe()
        {
            // Snapshot under lock to avoid concurrent modification from EnqueueThreadSafe
            // Uses reusable scratch list to avoid GC allocation (Plan §6.1)
            _drainThreadSafeScratch.Clear();
            lock (_lock)
            {
                _drainThreadSafeScratch.AddRange(_activeThreadSafeQueues);
            }
            for (int i = 0; i < _drainThreadSafeScratch.Count; i++)
            {
                _drainThreadSafeScratch[i].Drain(_signalBus);
            }
        }

        /// <summary>
        /// Drains all next-frame queued signals into the signal bus.
        /// Called from <c>Root.LateUpdate()</c>. Zero-allocation (uses reusable scratch list).
        /// </summary>
        public void DrainNextFrame()
        {
            // Snapshot under lock to avoid concurrent modification from EnqueueNextFrame
            // Uses reusable scratch list to avoid GC allocation (Plan §6.1)
            _drainNextFrameScratch.Clear();
            lock (_lock)
            {
                _drainNextFrameScratch.AddRange(_activeNextFrameQueues);
            }
            for (int i = 0; i < _drainNextFrameScratch.Count; i++)
            {
                _drainNextFrameScratch[i].Drain(_signalBus);
            }
        }

        /// <summary>Clears all queues (thread-safe and next-frame). Thread-safe.</summary>
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
