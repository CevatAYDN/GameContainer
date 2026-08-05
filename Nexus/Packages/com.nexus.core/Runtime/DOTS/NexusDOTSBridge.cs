#if UNITY_COLLECTIONS
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
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
    /// Thread-safe, lock-free, Job/Burst-compatible queue for queuing unmanaged signals inside Unity Jobs.
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

        /// <summary>
        /// Enqueues a signal thread-safely. Safe to call inside Job.Execute().
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
        private bool _isInitialized;

        public void Initialize(ISignalBus signalBus, Allocator allocator)
        {
            _signalBus = signalBus;
            _signalQueue = new NativeSignalQueue<T>(allocator);
            _isInitialized = true;
        }

        // Internal on purpose: the returned struct is a COPY sharing the same native handle —
        // disposing the copy would invalidate this bridge's queue. Nothing outside the
        // package needs it; callers must never Dispose the returned value.
        internal NativeSignalQueue<T> Queue => _signalQueue;

        private void Update()
        {
            if (_isInitialized && _signalQueue.IsCreated && _signalBus != null)
            {
                _signalQueue.Drain(_signalBus);
            }
        }

        private void OnDestroy()
        {
            if (_isInitialized)
            {
                _signalQueue.Dispose();
            }
        }
    }
}
#endif
