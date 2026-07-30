using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for integer memory protection.
    /// Obfuscates value in RAM using XOR encryption key to prevent GameGuardian / CheatEngine memory scans.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableInt
    {
        // _valueLock protects the (obscuredValue, cryptoKey) pair so a concurrent
        // getter never observes a crossed state between the two fields.
        private readonly object _valueLock = new();
        private int _obscuredValue;
        private int _cryptoKey;
        private List<Action<int, int>> _handlers;
        private Action<int, int>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng = System.Security.Cryptography.RandomNumberGenerator.Create();

        private static int GetSecureRandomKey()
        {
            // Use crypto RNG (thread-safe, no main-thread restriction).
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
            return Math.Max(key, 1000);
        }

        public SecureObservableInt(int initialValue = 0)
        {
            _cryptoKey = GetSecureRandomKey();
            _obscuredValue = initialValue ^ _cryptoKey;
        }

        public int Value
        {
            get
            {
                lock (_valueLock)
                {
                    return _obscuredValue ^ _cryptoKey;
                }
            }
            set
            {
                int old;
                lock (_valueLock)
                {
                    old = _obscuredValue ^ _cryptoKey;
                    if (old == value) return;
                    _cryptoKey = GetSecureRandomKey();
                    _obscuredValue = value ^ _cryptoKey;
                }

                Action<int, int>[] snapshot;
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
                    {
                        snapshot[i]?.Invoke(old, value);
                    }
                }
            }
        }

        public void SetWithoutNotify(int value)
        {
            lock (_valueLock)
            {
                _cryptoKey = GetSecureRandomKey();
                _obscuredValue = value ^ _cryptoKey;
            }
        }

        public void OnChanged(Action<int, int> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<int, int>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<int, int> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                {
                    _snapshotDirty = true;
                }
            }
        }

        public void ClearOnChanged()
        {
            lock (_handlersLock)
            {
                _handlers?.Clear();
                _snapshotCache = null;
                _snapshotDirty = false;
            }
        }

        public static implicit operator int(SecureObservableInt prop) => prop.Value;

        public override string ToString() => Value.ToString();
    }
}
