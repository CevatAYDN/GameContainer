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

        public NativeSignalQueue<T> Queue => _signalQueue;

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
