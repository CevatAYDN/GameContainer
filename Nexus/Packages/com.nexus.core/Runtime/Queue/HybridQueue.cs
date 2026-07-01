using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Represents a queued signal wrapper that can be fired into a signal bus and recycled.
    /// </summary>
    public interface IQueuedSignal
    {
        /// <summary>Fires the wrapped signal into the given signal bus.</summary>
        void Fire(SignalBus bus);
        /// <summary>Releases the wrapper back to the object pool.</summary>
        void Release();
    }

    /// <summary>
    /// Thread-safe object pool for reusing QueuedSignalWrapper instances to achieve 0 GC allocation.
    /// </summary>
    public static class QueuedSignalPool<T> where T : struct
    {
        private static readonly ConcurrentQueue<QueuedSignalWrapper<T>> s_pool = new();

        /// <summary>Rents a pooled wrapper initialized with the given signal.</summary>
        public static QueuedSignalWrapper<T> Rent(T signal)
        {
            if (!s_pool.TryDequeue(out var wrapper))
            {
                wrapper = new QueuedSignalWrapper<T>();
            }
            wrapper.Signal = signal;
            return wrapper;
        }

        /// <summary>Returns the wrapper to the pool and resets its payload.</summary>
        public static void Return(QueuedSignalWrapper<T> wrapper)
        {
            wrapper.Signal = default;
            s_pool.Enqueue(wrapper);
        }
    }

    /// <summary>
    /// A pooled class wrapper for signals to avoid boxing in ConcurrentQueue.
    /// </summary>
    public class QueuedSignalWrapper<T> : IQueuedSignal where T : struct
    {
        /// <summary>The wrapped signal payload.</summary>
        public T Signal;

        /// <summary>Fires the wrapped signal into the signal bus.</summary>
        public void Fire(SignalBus bus)
        {
            bus.Fire(Signal);
        }

        /// <summary>Releases the wrapper back to the pool.</summary>
        public void Release()
        {
            QueuedSignalPool<T>.Return(this);
        }
    }

    /// <summary>
    /// Manages thread-safe and next-frame deferred signal queues.
    /// Provides zero-allocation draining while preserving chronological interleaved order.
    /// Used by <see cref="Context"/> for cross-thread and deferred signal delivery.
    /// </summary>
    [Preserve]
    public class HybridQueue
    {
        private readonly SignalBus _signalBus;
        
        private readonly ConcurrentQueue<IQueuedSignal> _threadSafeQueue = new();
        private readonly ConcurrentQueue<IQueuedSignal> _nextFrameQueue = new();

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
            var wrapper = QueuedSignalPool<T>.Rent(signal);
            _threadSafeQueue.Enqueue(wrapper);
        }

        /// <summary>Enqueues a signal to be fired at the start of the next frame (LateUpdate drain).</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void EnqueueNextFrame<T>(T signal) where T : struct
        {
            var wrapper = QueuedSignalPool<T>.Rent(signal);
            _nextFrameQueue.Enqueue(wrapper);
        }

        /// <summary>
        /// Drains all thread-safe queued signals into the signal bus in chronological order.
        /// Called from <c>Root.Update()</c>. Zero-allocation.
        /// </summary>
        public void DrainThreadSafe()
        {
            while (_threadSafeQueue.TryDequeue(out var queuedSignal))
            {
                try
                {
                    queuedSignal.Fire(_signalBus);
                }
                finally
                {
                    queuedSignal.Release();
                }
            }
        }

        /// <summary>
        /// Drains all next-frame queued signals into the signal bus in chronological order.
        /// Called from <c>Root.LateUpdate()</c>. Zero-allocation.
        /// </summary>
        public void DrainNextFrame()
        {
            while (_nextFrameQueue.TryDequeue(out var queuedSignal))
            {
                try
                {
                    queuedSignal.Fire(_signalBus);
                }
                finally
                {
                    queuedSignal.Release();
                }
            }
        }

        /// <summary>Clears all queues. Thread-safe.</summary>
        public void Clear()
        {
            while (_threadSafeQueue.TryDequeue(out var queuedSignal))
            {
                queuedSignal.Release();
            }
            while (_nextFrameQueue.TryDequeue(out var queuedSignal))
            {
                queuedSignal.Release();
            }
        }
    }
}
