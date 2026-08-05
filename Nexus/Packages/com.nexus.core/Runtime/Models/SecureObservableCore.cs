using System;
using System.Collections.Generic;

namespace Nexus.Core
{
    /// <summary>
    /// Generic multicast-handler snapshot cache — the shared observer core for the whole
    /// observable family (SecureObservable*, ObservableProperty, ObservableList).
    /// Owns the handler list, the zero-GC snapshot cache, the dirty flag and the handler
    /// lock — the block that was previously copy-pasted across every observable type.
    /// Handler registration dedupes: registering the same delegate twice is a no-op.
    /// </summary>
    internal sealed class SnapshotDelegateSet<TDelegate>
    {
        private List<TDelegate> _handlers;
        // Audit fix 3.6: the snapshot is now published as a VOLATILE array reference and
        // rebuilt EAGERLY on mutation (Add/Remove/Clear), so GetSnapshot is a single
        // lock-free volatile read. The old lazy-dirty design took _handlersLock on EVERY
        // read — an ObservableProperty firing 1000×/frame with 10 subscribers paid 10k
        // lock acquisitions per frame. Mutations are rare against reads, so moving the
        // ToArray cost to the write side is a strict win. The published array is never
        // mutated after the volatile store, so lock-free readers always see a complete,
        // consistent handler set.
        private volatile TDelegate[] _snapshotCache;
        private readonly object _handlersLock = new();

        public void Add(TDelegate handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<TDelegate>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotCache = _handlers.ToArray();
                }
            }
        }

        public void Remove(TDelegate handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                {
                    _snapshotCache = _handlers.Count > 0 ? _handlers.ToArray() : null;
                }
            }
        }

        public void Clear()
        {
            lock (_handlersLock)
            {
                _handlers?.Clear();
                _snapshotCache = null;
            }
        }

        /// <summary>Returns the snapshot cache. Zero-GC AND lock-free on the hot path:
        /// the array is rebuilt on the write side whenever the handler set changes.</summary>
        public TDelegate[] GetSnapshot() => _snapshotCache;
    }

    /// <summary>
    /// Observer dispatch for the SecureObservable family. Thin wrapper over the shared
    /// <see cref="SnapshotDelegateSet{TDelegate}"/> core plus a <see cref="Notify"/> that
    /// matches the family's (old, new) change signature. <see cref="GetSnapshot"/> lets
    /// callers drive their own dispatch loop (e.g. ObservableProperty's reentrancy coalescing).
    /// </summary>
    internal sealed class SecureObserverSet<T>
    {
        private readonly SnapshotDelegateSet<Action<T, T>> _delegates = new();

        public void OnChanged(Action<T, T> handler) => _delegates.Add(handler);
        public void RemoveOnChanged(Action<T, T> handler) => _delegates.Remove(handler);
        public void Clear() => _delegates.Clear();

        /// <summary>Returns the current handler snapshot (zero-GC cached).</summary>
        public Action<T, T>[] GetSnapshot() => _delegates.GetSnapshot();

        /// <summary>Invokes the snapshot cache with the change. Zero-GC on the hot path:
        /// the array is rebuilt only when the handler set changed since the last notify.</summary>
        public void Notify(T oldValue, T newValue)
        {
            Action<T, T>[] snapshot = _delegates.GetSnapshot();
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i]?.Invoke(oldValue, newValue);
            }
        }
    }

    /// <summary>
    /// Shared RNG-backed dual-key generation for the secure observable family.
    /// One cryptographically secure RNG instance serves all wrappers (thread-safe),
    /// eliminating the per-type <c>s_rng</c> + key-pair copies.
    /// </summary>
    internal static class SecureKeyGen
    {
        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        // Audit fix 3.11: shared scratch buffers under a lock — every SecureObservable*
        // construction used to allocate 2× byte[4] (or byte[8]) per key-pair call.
        // Key generation is a boot-time path, so the lock is never contended in practice
        // and the steady-state allocation is now zero.
        private static readonly byte[] s_intBuf = new byte[4];
        private static readonly byte[] s_longBuf = new byte[8];
        private static readonly object s_bufLock = new();

        public static int NextIntKey()
        {
            lock (s_bufLock)
            {
                s_rng.GetBytes(s_intBuf);
                // Ensure non-zero to avoid degenerate XOR (key ^ 0 == key)
                int key = BitConverter.ToInt32(s_intBuf, 0) & 0x7FFFFFFF;
                return key != 0 ? key : 0x4E5855; // fallback "NXU"
            }
        }

        public static long NextLongKey()
        {
            lock (s_bufLock)
            {
                s_rng.GetBytes(s_longBuf);
                long key = BitConverter.ToInt64(s_longBuf, 0) & long.MaxValue;
                return key != 0 ? key : 0x4E4558554E5855L;
            }
        }

        public static (int key1, int key2) IntKeyPair()
        {
            int k1, k2;
            do { k1 = NextIntKey(); k2 = NextIntKey(); }
            while ((k1 ^ k2) == 0); // Ensure compound key is never zero
            return (k1, k2);
        }

        public static (long key1, long key2) LongKeyPair()
        {
            long k1, k2;
            do { k1 = NextLongKey(); k2 = NextLongKey(); }
            while ((k1 ^ k2) == 0);
            return (k1, k2);
        }
    }
}
