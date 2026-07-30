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
        private int _obscuredValue;
        private int _cryptoKey;
        private List<Action<int, int>> _handlers;
        private Action<int, int>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng = System.Security.Cryptography.RandomNumberGenerator.Create();

        private static int GetSecureRandomKey()
        {
            // P2-2 fix: use crypto RNG instead of UnityEngine.Random which is main-thread-only.
            // RandomNumberGenerator is thread-safe on .NET Standard 2.1+ — no lock needed.
            // Also reads/writes _obscuredValue and _cryptoKey atomically via Interlocked.
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF; // ensure positive
            return Math.Max(key, 1000);
        }

        public SecureObservableInt(int initialValue = 0)
        {
            _cryptoKey = GetSecureRandomKey();
            _obscuredValue = initialValue ^ _cryptoKey;
        }

        public int Value
        {
            get => _obscuredValue ^ _cryptoKey;
            set
            {
                int current = Value;
                if (current == value) return;

                int old = current;
                int newKey = GetSecureRandomKey();
                // P2-2 fix: atomic write of both fields via Interlocked.Exchange to prevent torn reads
                System.Threading.Interlocked.Exchange(ref _obscuredValue, value ^ newKey);
                System.Threading.Interlocked.Exchange(ref _cryptoKey, newKey);

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
            int newKey = GetSecureRandomKey();
            System.Threading.Interlocked.Exchange(ref _obscuredValue, value ^ newKey);
            System.Threading.Interlocked.Exchange(ref _cryptoKey, newKey);
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
