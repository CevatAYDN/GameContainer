using System;
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
    /// Central registry of per-type queued-signal pools so
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
        private class ReferenceComparer : IEqualityComparer<QueuedSignalWrapper<T>>
        {
            public static readonly ReferenceComparer Instance = new();
            public bool Equals(QueuedSignalWrapper<T> x, QueuedSignalWrapper<T> y) => ReferenceEquals(x, y);
            public int GetHashCode(QueuedSignalWrapper<T> obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly Stack<QueuedSignalWrapper<T>> s_pool = new();
        private static readonly HashSet<QueuedSignalWrapper<T>> s_pooledInstances = new(ReferenceComparer.Instance);
        private static readonly object s_poolLock = new();

        // Bound the pool and register clearing with NexusRuntime.Reset.
        private const int MaxPoolSize = 256;

        static QueuedSignalPool()
        {
            QueuedSignalPoolRegistry.Register(Clear);
        }

        /// <summary>Rents a pooled wrapper initialized with the given signal.</summary>
        public static QueuedSignalWrapper<T> Rent(T signal)
        {
            // Removed the redundant second null check (else branch).
            QueuedSignalWrapper<T> wrapper;
            lock (s_poolLock)
            {
                if (s_pool.Count > 0)
                {
                    wrapper = s_pool.Pop();
                    s_pooledInstances.Remove(wrapper);
                }
                else
                {
                    wrapper = new QueuedSignalWrapper<T>();
                }
                wrapper.Signal = signal;
            }
            return wrapper;
        }

        /// <summary>Returns the wrapper to the pool and resets its payload.</summary>
        public static void Return(QueuedSignalWrapper<T> wrapper)
        {
            if (wrapper == null) return;
            lock (s_poolLock)
            {
                wrapper.Signal = default;
                // Double-return guard: an instance already in the pool must not be pooled again
                if (!s_pooledInstances.Add(wrapper))
                {
                    return;
                }

                if (s_pool.Count < MaxPoolSize)
                {
                    s_pool.Push(wrapper);
                    return;
                }

                s_pooledInstances.Remove(wrapper);
            }
        }

        /// <summary>Empties the pool. Called via <see cref="QueuedSignalPoolRegistry"/> on runtime reset.</summary>
        public static void Clear()
        {
            lock (s_poolLock)
            {
                s_pool.Clear();
                s_pooledInstances.Clear();
            }
        }
    }

    /// <summary>
    /// A pooled class wrapper for signals to avoid boxing.
    /// </summary>
    public class QueuedSignalWrapper<T> : IQueuedSignal where T : struct
    {
        /// <summary>The wrapped signal payload.</summary>
        public T Signal;

        /// <summary>
        /// Fires the wrapped signal into the signal bus.
        /// </summary>
        /// <remarks>
        /// Important: <see cref="QueuedSignalWrapper{T}"/> is a pooled reference type that
        /// stores the signal payload as a value-type field. The value-type payload is copied
        /// when passed to <see cref="SignalBus.FireQueued{T}(T)"/>, so code that subsequently
        /// calls <see cref="Release"/> and resets the wrapper's Signal does not mutate the
        /// copy observed by the in-flight dispatch. This design relies on the value-type copy
        /// semantic to avoid races; do not change the wrapper to hold a reference to mutable
        /// shared state without revisiting this contract.
        /// </remarks>
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
    /// Zero-allocation ring buffer queue for storing IQueuedSignal items without GC churn.
    /// </summary>
    internal class QueuedSignalRingBuffer
    {
        private IQueuedSignal[] _items;
        private int _head;
        private int _tail;
        private int _count;

        public QueuedSignalRingBuffer(int initialCapacity = 256)
        {
            _items = new IQueuedSignal[initialCapacity];
        }

        public int Count => _count;

        public void Enqueue(IQueuedSignal item)
        {
            if (_count == _items.Length)
            {
                var newItems = new IQueuedSignal[_items.Length * 2];
                for (int i = 0; i < _count; i++)
                {
                    newItems[i] = _items[(_head + i) % _items.Length];
                }
                _items = newItems;
                _head = 0;
                _tail = _count;
            }
            _items[_tail] = item;
            _tail = (_tail + 1) % _items.Length;
            _count++;
        }

        public IQueuedSignal Dequeue()
        {
            if (_count == 0) return null;
            var item = _items[_head];
            _items[_head] = null;
            _head = (_head + 1) % _items.Length;
            _count--;
            return item;
        }

        public void Clear()
        {
            while (_count > 0)
            {
                var item = Dequeue();
                item?.Release();
            }
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
        
        private readonly QueuedSignalRingBuffer _threadSafeQueue = new(256);
        // The active queue receives new next-frame signals.  DrainNextFrame swaps it with
        // the detached queue before dispatch, so a handler that enqueues another next-frame
        // signal cannot have that signal consumed by the current drain.
        private QueuedSignalRingBuffer _nextFrameQueue = new(256);
        private QueuedSignalRingBuffer _nextFrameDrainQueue = new(256);
        private readonly object _threadSafeLock = new();
        private readonly object _nextFrameLock = new();

        // Editor introspection (G-2): live queue depth + cumulative throughput.
        private long _totalEnqueued;
        private long _totalDrained;

        /// <summary>Current number of signals waiting in the thread-safe queue.</summary>
        public int ThreadSafeQueueDepth { get { lock (_threadSafeLock) return _threadSafeQueue.Count; } }
        /// <summary>Current number of signals waiting in the next-frame queue.</summary>
        public int NextFrameQueueDepth { get { lock (_nextFrameLock) return _nextFrameQueue.Count; } }
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
            lock (_threadSafeLock)
            {
                _threadSafeQueue.Enqueue(wrapper);
            }
            System.Threading.Interlocked.Increment(ref _totalEnqueued);
        }

        /// <summary>Enqueues a signal to be fired at the start of the next frame (LateUpdate drain).</summary>
        /// <typeparam name="T">The signal struct type.</typeparam>
        /// <param name="signal">The signal data.</param>
        public void EnqueueNextFrame<T>(T signal) where T : struct
        {
            var wrapper = QueuedSignalPool<T>.Rent(signal);
            lock (_nextFrameLock)
            {
                _nextFrameQueue.Enqueue(wrapper);
            }
            System.Threading.Interlocked.Increment(ref _totalEnqueued);
        }

        // Reentrancy guard. Drain releases the queue lock between dequeue
        // and dispatch (by design, see Audit note 2.5), so a second caller entering Drain
        // concurrently (Drain is public API) would interleave dispatches and break the
        // chronological-order guarantee. In practice QueueDrainer drives this from one
        // thread, but the guard makes the invariant structural instead of conventional.
        // The two queues are independent — each gets its OWN flag, so a DrainNextFrame is
        // never silently skipped just because a DrainThreadSafe is in flight.
        private int _threadSafeDrainInProgress;
        private int _nextFrameDrainInProgress;

        /// <summary>
        /// Drains all thread-safe queued signals into the signal bus in chronological order.
        /// Called from <c>Root.Update()</c>. Zero-allocation.
        /// </summary>
        public void DrainThreadSafe()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _threadSafeDrainInProgress, 1, 0) != 0)
                return; // another drain is already in progress — queued items will be picked up by it
            try { Drain(_threadSafeQueue, _threadSafeLock); }
            finally { System.Threading.Interlocked.Exchange(ref _threadSafeDrainInProgress, 0); }
        }

        /// <summary>
        /// Drains all next-frame queued signals into the signal bus in chronological order.
        /// Called from <c>Root.LateUpdate()</c>. Zero-allocation.
        /// </summary>
        public void DrainNextFrame()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _nextFrameDrainInProgress, 1, 0) != 0)
                return;
            try
            {
                QueuedSignalRingBuffer queueToDrain;
                lock (_nextFrameLock)
                {
                    queueToDrain = _nextFrameQueue;
                    _nextFrameQueue = _nextFrameDrainQueue;
                    _nextFrameDrainQueue = queueToDrain;
                }

                // queueToDrain is detached from producers for the whole drain.  Drain keeps
                // the lock boundary for Clear() and diagnostics, but all reentrant enqueues
                // target the newly active queue above and therefore wait for the next call.
                Drain(queueToDrain, _nextFrameLock);
            }
            finally { System.Threading.Interlocked.Exchange(ref _nextFrameDrainInProgress, 0); }
        }

        private void Drain(QueuedSignalRingBuffer queue, object queueLock)
        {
            // Audit note 2.5 (intentional design, documented): the lock is RELEASED between
            // the dequeue and the dispatch so a concurrent EnqueueThreadSafe is never blocked
            // behind a slow handler — a producer arriving mid-drain is processed in the same
            // frame. Consequence: _totalDrained increments in the finally AFTER dispatch, not
            // in the same atomic step as the dequeue, so the dashboard counters
            // (_totalEnqueued vs _totalDrained) can TRANSIENTLY disagree while a drain is in
            // flight. They converge when the queue empties; do not treat a momentary mismatch
            // as a lost signal.
            while (true)
            {
                IQueuedSignal queuedSignal = null;
                lock (queueLock)
                {
                    if (queue.Count > 0) queuedSignal = queue.Dequeue();
                }
                if (queuedSignal == null) break;

                try
                {
                    queuedSignal.Fire(_signalBus);
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Exception during queued signal drain: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    queuedSignal.Release();
                    System.Threading.Interlocked.Increment(ref _totalDrained);
                }
            }
        }

        /// <summary>Clears all pending signals from both queues. Called on context dispose.</summary>
        public void Clear()
        {
            lock (_threadSafeLock)
            {
                _threadSafeQueue.Clear();
            }
            lock (_nextFrameLock)
            {
                _nextFrameQueue.Clear();
                _nextFrameDrainQueue.Clear();
            }
        }
    }
}
