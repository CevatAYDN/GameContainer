#if UNITY_COLLECTIONS
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Unity.Jobs;
using Nexus.Core;

namespace Nexus.DOTS
{
    /// <summary>
    /// Captures Unity's main-thread id at startup so <see cref="NativeSignalQueue{T}.Drain"/>
    /// can verify its caller in ALL build types. The previous check compared against a
    /// hard-coded id of 1 (not guaranteed by Unity) via UnityEngine.Assertions, which is
    /// stripped from release builds.
    /// </summary>
    internal static class NexusDOTSMainThread
    {
        /// <summary>Main-thread id, or -1 when not captured yet (check is skipped then).</summary>
        internal static int MainThreadId = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Capture()
        {
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
    }

    /// <summary>
    /// A <see cref="NativeQueue{T}"/> of unmanaged signals: jobs write through <see cref="AsParallelWriter"/>,
    /// the main thread drains into the SignalBus. The package neither Burst-compiles nor Burst-tests it (#109).
    /// Bridges the high-performance Data-Oriented Technology Stack (DOTS) to the observable OOP Signal Bus.
    /// </summary>
    public struct NativeSignalQueue<T> : IDisposable where T : unmanaged
    {
        private NativeQueue<T> _queue;
        private readonly Allocator _allocator;

        public NativeSignalQueue(Allocator allocator)
        {
            _queue = new NativeQueue<T>(allocator);
            _allocator = allocator;
        }

        public bool IsCreated => _queue.IsCreated;

        internal bool TryDequeue(out T signal) => _queue.TryDequeue(out signal);

        /// <summary>
        /// Enqueues from one exclusive writer. Use AsParallelWriter for concurrent jobs,
        /// and complete all producers before draining or disposing the queue.
        /// </summary>
        public void Enqueue(T signal)
        {
            _queue.Enqueue(signal);
        }

        /// <summary>
        /// Dequeues and dispatches all queued signals into the OOP SignalBus.
        /// Must be called from the Main Thread (e.g. inside a System Update or MonoBehaviour Update).
        /// </summary>
        public void Drain(ISignalBus signalBus)
        {
            if (!_queue.IsCreated) return;

            int mainThreadId = NexusDOTSMainThread.MainThreadId;
            if (mainThreadId != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new InvalidOperationException(
                    "[Nexus DOTS] NativeSignalQueue.Drain() must be called from the main thread. Use DOTSSignalBridge.Update() instead.");
            }

            while (_queue.TryDequeue(out T signal))
            {
                signalBus.Fire(signal);
            }
        }

        /// <summary>
        /// Parallel writer wrapper for writing from concurrent Jobs.
        /// </summary>
        public NativeQueue<T>.ParallelWriter AsParallelWriter()
        {
            return _queue.AsParallelWriter();
        }

        public void Dispose()
        {
            if (_queue.IsCreated)
            {
                _queue.Dispose();
            }
        }
    }

    /// <summary>
    /// Component that automatically drains a registered NativeSignalQueue every frame on the main thread.
    /// </summary>
    public class DOTSSignalBridge<T> : MonoBehaviour where T : unmanaged
    {
        private NativeSignalQueue<T> _signalQueue;
        private ISignalBus _signalBus;
        private Allocator _allocator;
        private bool _isInitialized;
        // Unity calls OnDestroy only on an object whose Awake ran, so the native queue exists only from then on: a bridge
        // on an object that is never active owns no native memory (issue #192).
        private bool _awake;
        private JobHandle _producers;

        private void Awake()
        {
            _awake = true;
            if (_isInitialized && !_signalQueue.IsCreated) _signalQueue = new NativeSignalQueue<T>(_allocator);
        }

        public void Initialize(ISignalBus signalBus, Allocator allocator)
        {
            EnsureMainThread();
            if (signalBus == null) throw new ArgumentNullException(nameof(signalBus));
            _producers.Complete();
            _signalQueue.Dispose();
            _signalBus = signalBus;
            _allocator = allocator;
            _isInitialized = true;
            // On an object that has not been active yet the queue is created by Awake (see _awake).
            if (_awake) _signalQueue = new NativeSignalQueue<T>(allocator);
        }

        /// <summary>Returns true if the bridge has been initialized and its native queue is allocated (an object that has
        /// not been active yet has no queue).</summary>
        public bool IsInitialized => _isInitialized && _signalQueue.IsCreated;

        /// <summary>
        /// Enqueues on the main thread after completing registered producers.
        /// Jobs must use AsParallelWriter and register their scheduled JobHandle.
        /// </summary>
        public void Enqueue(T signal)
        {
            EnsureMainThread();
            if (!_isInitialized || !_signalQueue.IsCreated)
                throw NotReady();
            _producers.Complete();
            _producers = default;
            _signalQueue.Enqueue(signal);
        }

        /// <summary>
        /// Obtains a writer on the main thread. Immediately register every scheduled
        /// producer using AddProducerDependency before returning control to Unity.
        /// </summary>
        public NativeQueue<T>.ParallelWriter AsParallelWriter()
        {
            EnsureMainThread();
            if (!_isInitialized || !_signalQueue.IsCreated)
                throw NotReady();
            return _signalQueue.AsParallelWriter();
        }

        private InvalidOperationException NotReady() => new InvalidOperationException(_isInitialized
            ? $"[Nexus DOTS] DOTSSignalBridge<{typeof(T).Name}> has no queue yet: it is created when its GameObject first becomes active."
            : $"[Nexus DOTS] DOTSSignalBridge<{typeof(T).Name}> is not initialized.");

        /// <summary>Registers producer ownership so Update, reinitialization and destruction complete jobs before accessing native storage.</summary>
        public void AddProducerDependency(JobHandle producer)
        {
            EnsureMainThread();
            if (!IsInitialized) throw new InvalidOperationException("The DOTS bridge is not initialized.");
            _producers = JobHandle.CombineDependencies(_producers, producer);
        }

        private static void EnsureMainThread()
        {
            int id = NexusDOTSMainThread.MainThreadId;
            if (id != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId != id)
                throw new InvalidOperationException("DOTS bridge ownership APIs must be called on the main thread.");
        }

        /// <summary>
        /// Manually drains all queued signals into the SignalBus on the main thread.
        /// </summary>
        public void Drain()
        {
            EnsureMainThread();
            while (_isInitialized && _signalQueue.IsCreated && _signalBus != null)
            {
                // A dispatched handler can register another producer before the next dequeue.
                _producers.Complete();
                _producers = default;
                if (!_signalQueue.TryDequeue(out T signal)) break;
                _signalBus.Fire(signal);
            }
        }

        // Internal property kept for internal/testing access.
        internal NativeSignalQueue<T> Queue => _signalQueue;

        private void Update()
        {
            Drain();
        }

        private void OnDestroy()
        {
            if (_isInitialized)
            {
                _producers.Complete();
                _signalQueue.Dispose();
                _isInitialized = false;
            }
        }
    }
}
#endif
