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
    /// P1-15 fix: central registry of per-type queued-signal pools so
    /// <see cref="NexusRuntime.Reset"/> can clear them all (previously the static
    /// pools were only reset on domain reload).
    /// </summary>
    internal static class QueuedSignalPoolRegistry
    {
        private static readonly List<Action> s_clearActions = new();
        private static readonly object s_lock = new();

        public static void Register(Action clearAction)
        {
            lock (s_lock)
            {
                s_clearActions.Add(clearAction);
            }
        }

        public static void ClearAll()
        {
            lock (s_lock)
            {
                for (int i = 0; i < s_clearActions.Count; i++)
                {
                    s_clearActions[i]();
                }
            }
        }
    }

    /// <summary>
    /// Thread-safe object pool for reusing QueuedSignalWrapper instances to achieve 0 GC allocation.
    /// </summary>
    public static class QueuedSignalPool<T> where T : struct
    {
        private static readonly ConcurrentQueue<QueuedSignalWrapper<T>> s_pool = new();

        // P1-15 fix: bound the pool and register clearing with NexusRuntime.Reset.
        private const int MaxPoolSize = 256;

        static QueuedSignalPool()
        {
            QueuedSignalPoolRegistry.Register(Clear);
        }

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
            if (s_pool.Count < MaxPoolSize)
            {
                s_pool.Enqueue(wrapper);
            }
        }

        /// <summary>Empties the pool. Called via <see cref="QueuedSignalPoolRegistry"/> on runtime reset.</summary>
        public static void Clear()
        {
            while (s_pool.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// A pooled class wrapper for signals to avoid boxing in ConcurrentQueue.
    /// </summary>
    public class QueuedSignalWrapper<T> : IQueuedSignal where T : struct
    {
        /// <summary>The wrapped signal payload.</summary>
        public T Signal;

        /// <summary>
        /// Fires the wrapped signal into the signal bus.
        /// P0-4 fix: routes through the async-aware queued dispatch so signals with
        /// async handlers do not throw <see cref="NexusSyncAsyncMismatchException"/> on drain.
        /// </summary>
        public void Fire(SignalBus bus)
        {
            bus.FireQueued(Signal);
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

        // Editor introspection (G-2): live queue depth + cumulative throughput.
        // Counters use Interlocked for cross-thread correctness; reads are lock-free.
        private long _totalEnqueued;
        private long _totalDrained;

        /// <summary>Current number of signals waiting in the thread-safe queue.</summary>
        public int ThreadSafeQueueDepth => _threadSafeQueue.Count;
        /// <summary>Current number of signals waiting in the next-frame queue.</summary>
        public int NextFrameQueueDepth => _nextFrameQueue.Count;
        /// <summary>Total signals enqueued across both queues since creation.</summary>
        public long TotalEnqueued => System.Threading.Interlocked.Read(ref _totalEnqueued);
        /// <summary>Total signals drained (dispatched) since creation.</summary>
        public long TotalDrained => System.Threading.Interlocked.Read(ref _totalDrained);

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
            System.Threading.Interlocked.Increment(ref _totalEnqueued);
        }

        /// <summary>Enqueues a signal to be fired at the start of the next frame (LateUpdate drain).</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void EnqueueNextFrame<T>(T signal) where T : struct
        {
            var wrapper = QueuedSignalPool<T>.Rent(signal);
            _nextFrameQueue.Enqueue(wrapper);
            System.Threading.Interlocked.Increment(ref _totalEnqueued);
        }

        /// <summary>
        /// Drains all thread-safe queued signals into the signal bus in chronological order.
        /// Called from <c>Root.Update()</c>. Zero-allocation.
        /// P1-15 fix: the drain is capped at the queue's size at drain start, so a handler
        /// that re-enqueues during the drain cannot livelock the frame; those signals run
        /// next frame. P0-4 fix: per-item exceptions are logged and the drain continues.
        /// </summary>
        public void DrainThreadSafe()
        {
            Drain(_threadSafeQueue);
        }

        /// <summary>
        /// Drains all next-frame queued signals into the signal bus in chronological order.
        /// Called from <c>Root.LateUpdate()</c>. Zero-allocation.
        /// P1-15 fix: signals enqueued during the drain are deferred to the next frame
        /// (count snapshot), restoring "next frame" semantics.
        /// </summary>
        public void DrainNextFrame()
        {
            Drain(_nextFrameQueue);
        }

        private void Drain(ConcurrentQueue<IQueuedSignal> queue)
        {
            int max = queue.Count;
            for (int i = 0; i < max; i++)
            {
                if (!queue.TryDequeue(out var queuedSignal)) break;
                try
                {
                    queuedSignal.Fire(_signalBus);
                }
                catch (Exception ex)
                {
                    // One failing signal must not abort the rest of the drain.
                    NexusRuntime.Logger?.LogError($"[Nexus] Queued signal dispatch failed during drain: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    queuedSignal.Release();
                    System.Threading.Interlocked.Increment(ref _totalDrained);
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
