using System;
using System.Collections.Generic;

namespace Nexus.Core
{
    /// <summary>
    /// Shared observer dispatch machinery for the secure observable family.
    /// Owns the handler list, the zero-GC snapshot cache, the dirty flag and the
    /// handler lock — the block that was previously copy-pasted across all five
    /// SecureObservable* types. The value-type-specific masking stays in the thin
    /// wrappers; everything about "who gets notified and when" lives here once.
    /// </summary>
    internal sealed class SecureObserverSet<T>
    {
        private List<Action<T, T>> _handlers;
        private Action<T, T>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        public void OnChanged(Action<T, T> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<T, T>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<T, T> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                    _snapshotDirty = true;
            }
        }

        public void Clear()
        {
            lock (_handlersLock)
            {
                _handlers?.Clear();
                _snapshotCache = null;
                _snapshotDirty = false;
            }
        }

        /// <summary>Invokes the snapshot cache with the change. Zero-GC on the hot path:
        /// the array is rebuilt only when the handler set changed since the last notify.</summary>
        public void Notify(T oldValue, T newValue)
        {
            Action<T, T>[] snapshot;
            lock (_handlersLock)
            {
                if (_snapshotDirty)
                {
                    _snapshotCache = _handlers != null && _handlers.Count > 0 ? _handlers.ToArray() : null;
                    _snapshotDirty = false;
                }
                snapshot = _snapshotCache;
            }
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

        public static int NextIntKey()
        {
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            // Ensure non-zero to avoid degenerate XOR (key ^ 0 == key)
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
            return key != 0 ? key : 0x4E5855; // fallback "NXU"
        }

        public static long NextLongKey()
        {
            byte[] bytes = new byte[8];
            s_rng.GetBytes(bytes);
            long key = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            return key != 0 ? key : 0x4E4558554E5855L;
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
