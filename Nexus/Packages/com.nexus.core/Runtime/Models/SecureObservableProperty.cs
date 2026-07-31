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

    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for 64-bit integer memory protection.
    /// Obfuscates value in RAM using XOR encryption key to prevent GameGuardian / CheatEngine memory scans.
    /// Mirrors <see cref="SecureObservableInt"/> for <see cref="long"/> balances (e.g. economy currencies).
    /// </summary>
    [Preserve]
    public sealed class SecureObservableLong
    {
        // _valueLock protects the (obscuredValue, cryptoKey) pair so a concurrent
        // getter never observes a crossed state between the two fields.
        private readonly object _valueLock = new();
        private long _obscuredValue;
        private long _cryptoKey;
        private List<Action<long, long>> _handlers;
        private Action<long, long>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng = System.Security.Cryptography.RandomNumberGenerator.Create();

        private static long GetSecureRandomKey()
        {
            byte[] bytes = new byte[8];
            s_rng.GetBytes(bytes);
            long key = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            return Math.Max(key, 1000L);
        }

        public SecureObservableLong(long initialValue = 0)
        {
            _cryptoKey = GetSecureRandomKey();
            _obscuredValue = initialValue ^ _cryptoKey;
        }

        public long Value
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
                long old;
                lock (_valueLock)
                {
                    old = _obscuredValue ^ _cryptoKey;
                    if (old == value) return;
                    _cryptoKey = GetSecureRandomKey();
                    _obscuredValue = value ^ _cryptoKey;
                }

                Action<long, long>[] snapshot;
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

        public void SetWithoutNotify(long value)
        {
            lock (_valueLock)
            {
                _cryptoKey = GetSecureRandomKey();
                _obscuredValue = value ^ _cryptoKey;
            }
        }

        public void OnChanged(Action<long, long> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<long, long>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<long, long> handler)
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

        public static implicit operator long(SecureObservableLong prop) => prop.Value;

        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for single-precision float memory protection.
    /// Obfuscates the IEEE-754 bit pattern in RAM via XOR encryption key to prevent GameGuardian /
    /// CheatEngine memory scans. Mirrors <see cref="SecureObservableInt"/> / <see cref="SecureObservableLong"/>
    /// for floats (e.g. AdService interstitial cooldown timestamps a cheater could otherwise zero out).
    /// </summary>
    [Preserve]
    public sealed class SecureObservableFloat
    {
        // _valueLock protects the (obscuredValue, cryptoKey) pair so a concurrent
        // getter never observes a crossed state between the two fields.
        private readonly object _valueLock = new();
        private int _obscuredValue;
        private int _cryptoKey;
        private List<Action<float, float>> _handlers;
        private Action<float, float>[] _snapshotCache;
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

        // Zero-allocation float <-> int bit re-interpretation. Explicit-layout union is
        // CLS-compliant and needs no unsafe block, unlike pointer casts or byte[] boxing.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBitsUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float AsFloat;
            [System.Runtime.InteropServices.FieldOffset(0)] public int AsInt;
        }

        private static int FloatToIntBits(float value)
        {
            var u = new FloatBitsUnion { AsFloat = value };
            return u.AsInt;
        }

        private static float IntToFloatBits(int bits)
        {
            var u = new FloatBitsUnion { AsInt = bits };
            return u.AsFloat;
        }

        public SecureObservableFloat(float initialValue = 0f)
        {
            _cryptoKey = GetSecureRandomKey();
            _obscuredValue = FloatToIntBits(initialValue) ^ _cryptoKey;
        }

        public float Value
        {
            get
            {
                lock (_valueLock)
                {
                    return IntToFloatBits(_obscuredValue ^ _cryptoKey);
                }
            }
            set
            {
                float old;
                lock (_valueLock)
                {
                    old = IntToFloatBits(_obscuredValue ^ _cryptoKey);
                    if (old == value) return;
                    _cryptoKey = GetSecureRandomKey();
                    _obscuredValue = FloatToIntBits(value) ^ _cryptoKey;
                }

                Action<float, float>[] snapshot;
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

        public void SetWithoutNotify(float value)
        {
            lock (_valueLock)
            {
                _cryptoKey = GetSecureRandomKey();
                _obscuredValue = FloatToIntBits(value) ^ _cryptoKey;
            }
        }

        public void OnChanged(Action<float, float> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<float, float>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<float, float> handler)
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

        public static implicit operator float(SecureObservableFloat prop) => prop.Value;

        public override string ToString() => Value.ToString();
    }
}
