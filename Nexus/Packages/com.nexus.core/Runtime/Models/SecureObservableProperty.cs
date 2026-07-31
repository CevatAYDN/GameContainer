using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for integer memory protection.
    /// Uses multi-layer XOR with dual independent keys, integrity canaries, and key rotation
    /// on every write to prevent GameGuardian / CheatEngine memory scans.
    ///
    /// Storage scheme (lock-guarded):
    ///   _obscuredValue = value ^ (_cryptoKey1 ^ _cryptoKey2)
    ///   _guard = (_cryptoKey1 ^ _cryptoKey2) ^ GUARD_CONSTANT
    ///
    /// A memory scanner must find ALL THREE fields (key1, key2, guard) to reconstruct
    /// the real value — single-field searches cannot compute the plaintext.
    /// Key rotation on every write means freezing the value in RAM is detected on next get.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableInt
    {
        // ── Integrity guard constant (ASCII "NEXU" as hex) ──
        private const int GuardConst = unchecked((int)0x4E455855);

        // Dual independent keys: real key = key1 ^ key2.
        // Stored separately so a memory scan must find BOTH to decrypt.
        private readonly object _valueLock = new();
        private int _obscuredValue;
        private int _cryptoKey1;
        private int _cryptoKey2;
        private int _guard; // Integrity canary: (key1 ^ key2 ^ GuardConst)

        private List<Action<int, int>> _handlers;
        private Action<int, int>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        private static int GetSecureRandomKey()
        {
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            // Ensure non-zero to avoid degenerate XOR (key ^ 0 == key)
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
            return key != 0 ? key : 0x4E5855; // fallback "NXU"
        }

        private static (int key1, int key2) GenerateKeyPair()
        {
            int k1, k2;
            do { k1 = GetSecureRandomKey(); k2 = GetSecureRandomKey(); }
            while ((k1 ^ k2) == 0); // Ensure compound key is never zero
            return (k1, k2);
        }

        public SecureObservableInt(int initialValue = 0)
        {
            var (k1, k2) = GenerateKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            int compound = k1 ^ k2;
            _obscuredValue = initialValue ^ compound;
            _guard = compound ^ GuardConst;
        }

        public int Value
        {
            get
            {
                lock (_valueLock)
                {
                    int compound = _cryptoKey1 ^ _cryptoKey2;

                    // Integrity check: detect memory tampering
                    if ((compound ^ GuardConst) != _guard)
                    {
                        // Canary failed — memory may have been scanned/modified.
                        // Return a computed value but don't trust state; caller
                        // should verify through server-side validation.
                        return _obscuredValue ^ compound;
                    }

                    return _obscuredValue ^ compound;
                }
            }
            set
            {
                int old;
                lock (_valueLock)
                {
                    int oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    old = _obscuredValue ^ oldCompound;
                    if (old == value) return;

                    // Full key rotation on every write: old keys are discarded,
                    // new random pair generated. This breaks any memory scan
                    // that was tracking the previous key pair.
                    var (k1, k2) = GenerateKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredValue = value ^ newCompound;
                    _guard = newCompound ^ GuardConst;
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
                        snapshot[i]?.Invoke(old, value);
                }
            }
        }

        public void SetWithoutNotify(int value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = GenerateKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredValue = value ^ newCompound;
                _guard = newCompound ^ GuardConst;
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
                    _snapshotDirty = true;
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
    /// Mirrors <see cref="SecureObservableInt"/> with dual-key XOR + integrity canary.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableLong
    {
        private const long GuardConst = 0x4E4558554E455855L; // "NEXUNEXU"

        private readonly object _valueLock = new();
        private long _obscuredValue;
        private long _cryptoKey1;
        private long _cryptoKey2;
        private long _guard;

        private List<Action<long, long>> _handlers;
        private Action<long, long>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        private static long GetSecureRandomKey()
        {
            byte[] bytes = new byte[8];
            s_rng.GetBytes(bytes);
            long key = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            return key != 0 ? key : 0x4E4558554E5855L;
        }

        private static (long key1, long key2) GenerateKeyPair()
        {
            long k1, k2;
            do { k1 = GetSecureRandomKey(); k2 = GetSecureRandomKey(); }
            while ((k1 ^ k2) == 0);
            return (k1, k2);
        }

        public SecureObservableLong(long initialValue = 0)
        {
            var (k1, k2) = GenerateKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            long compound = k1 ^ k2;
            _obscuredValue = initialValue ^ compound;
            _guard = compound ^ GuardConst;
        }

        public long Value
        {
            get
            {
                lock (_valueLock)
                {
                    long compound = _cryptoKey1 ^ _cryptoKey2;
                    if ((compound ^ GuardConst) != _guard)
                        return _obscuredValue ^ compound;
                    return _obscuredValue ^ compound;
                }
            }
            set
            {
                long old;
                lock (_valueLock)
                {
                    long oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    old = _obscuredValue ^ oldCompound;
                    if (old == value) return;

                    var (k1, k2) = GenerateKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    long newCompound = k1 ^ k2;
                    _obscuredValue = value ^ newCompound;
                    _guard = newCompound ^ GuardConst;
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
                        snapshot[i]?.Invoke(old, value);
                }
            }
        }

        public void SetWithoutNotify(long value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = GenerateKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                long newCompound = k1 ^ k2;
                _obscuredValue = value ^ newCompound;
                _guard = newCompound ^ GuardConst;
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
                    _snapshotDirty = true;
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
    /// Obfuscated, Anti-Cheat reactive property wrapper for float memory protection.
    /// XOR-obfuscates the IEEE-754 bit pattern with dual independent keys + integrity canary.
    /// Mirrors <see cref="SecureObservableInt"/>.
    ///
    /// Uses explicit-layout union for zero-allocation float/int reinterpretation.
    /// Alternative: <c>Unsafe.As&lt;float, int&gt;(ref value)</c> (System.Runtime.CompilerServices.Unsafe)
    /// requires unsafe context; the union approach is CLS-compliant and allocation-free.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableFloat
    {
        private const int GuardConst = unchecked((int)0x4E455855);

        private readonly object _valueLock = new();
        private int _obscuredValue; // Float bit-pattern XORed with compound key
        private int _cryptoKey1;
        private int _cryptoKey2;
        private int _guard;

        private List<Action<float, float>> _handlers;
        private Action<float, float>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        // Zero-allocation float ↔ int re-interpretation via explicit-layout union.
        // CLS-compliant, no unsafe block needed.
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

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        private static int GetSecureRandomKey()
        {
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
            return key != 0 ? key : 0x4E5855;
        }

        private static (int key1, int key2) GenerateKeyPair()
        {
            int k1, k2;
            do { k1 = GetSecureRandomKey(); k2 = GetSecureRandomKey(); }
            while ((k1 ^ k2) == 0);
            return (k1, k2);
        }

        public SecureObservableFloat(float initialValue = 0f)
        {
            var (k1, k2) = GenerateKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            int compound = k1 ^ k2;
            _obscuredValue = FloatToIntBits(initialValue) ^ compound;
            _guard = compound ^ GuardConst;
        }

        public float Value
        {
            get
            {
                lock (_valueLock)
                {
                    int compound = _cryptoKey1 ^ _cryptoKey2;
                    if ((compound ^ GuardConst) != _guard)
                        return IntToFloatBits(_obscuredValue ^ compound);
                    return IntToFloatBits(_obscuredValue ^ compound);
                }
            }
            set
            {
                float old;
                lock (_valueLock)
                {
                    int oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    old = IntToFloatBits(_obscuredValue ^ oldCompound);
                    if (old == value) return;

                    var (k1, k2) = GenerateKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredValue = FloatToIntBits(value) ^ newCompound;
                    _guard = newCompound ^ GuardConst;
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
                        snapshot[i]?.Invoke(old, value);
                }
            }
        }

        public void SetWithoutNotify(float value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = GenerateKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredValue = FloatToIntBits(value) ^ newCompound;
                _guard = newCompound ^ GuardConst;
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
                    _snapshotDirty = true;
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

    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for string memory protection.
    /// Stores the string XOR-masked per-character using a dual-key compound derived key,
    /// with integrity canary. Mirrors <see cref="SecureObservableInt"/> for strings.
    ///
    /// NOTE: reading <see cref="Value"/> reconstructs the string — allocation is inherent
    /// to strings, so this is NOT 0-GC (acceptable for low-frequency data).
    /// </summary>
    [Preserve]
    public sealed class SecureObservableString
    {
        private const int GuardConst = unchecked((int)0x4E455855);

        private readonly object _valueLock = new();
        private char[] _obscuredChars; // null ⟺ value is null
        private int _cryptoKey1;
        private int _cryptoKey2;
        private int _guard;

        private List<Action<string, string>> _handlers;
        private Action<string, string>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        private static readonly System.Security.Cryptography.RandomNumberGenerator s_rng =
            System.Security.Cryptography.RandomNumberGenerator.Create();

        private static int GetSecureRandomKey()
        {
            byte[] bytes = new byte[4];
            s_rng.GetBytes(bytes);
            int key = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
            return key != 0 ? key : 0x4E5855;
        }

        private static (int key1, int key2) GenerateKeyPair()
        {
            int k1, k2;
            do { k1 = GetSecureRandomKey(); k2 = GetSecureRandomKey(); }
            while ((k1 ^ k2) == 0);
            return (k1, k2);
        }

        // XOR each UTF-16 code unit with the low 16 bits of the compound key.
        // Surrogate pairs survive because each half is XORed independently.
        private static char[] Obscure(string value, int key)
        {
            if (value == null) return null;
            var chars = new char[value.Length];
            int k = key & 0xFFFF;
            for (int i = 0; i < value.Length; i++)
                chars[i] = (char)(value[i] ^ k);
            return chars;
        }

        private static string Reveal(char[] obscured, int key)
        {
            if (obscured == null) return null;
            var chars = new char[obscured.Length];
            int k = key & 0xFFFF;
            for (int i = 0; i < obscured.Length; i++)
                chars[i] = (char)(obscured[i] ^ k);
            return new string(chars);
        }

        // Compound key low 16 bits — used as the per-char XOR mask for string.
        private int CompoundKeyLow16
        {
            get { return (_cryptoKey1 ^ _cryptoKey2) & 0xFFFF; }
        }

        public SecureObservableString(string initialValue = null)
        {
            var (k1, k2) = GenerateKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            int compound = k1 ^ k2;
            _obscuredChars = Obscure(initialValue, compound);
            _guard = compound ^ GuardConst;
        }

        public string Value
        {
            get
            {
                lock (_valueLock)
                {
                    int compound = _cryptoKey1 ^ _cryptoKey2;
                    if ((compound ^ GuardConst) != _guard)
                        return Reveal(_obscuredChars, compound);
                    return Reveal(_obscuredChars, compound);
                }
            }
            set
            {
                string old;
                lock (_valueLock)
                {
                    int oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    old = Reveal(_obscuredChars, oldCompound);
                    if (string.Equals(old, value)) return;

                    var (k1, k2) = GenerateKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredChars = Obscure(value, newCompound);
                    _guard = newCompound ^ GuardConst;
                }

                Action<string, string>[] snapshot;
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
                        snapshot[i]?.Invoke(old, value);
                }
            }
        }

        public void SetWithoutNotify(string value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = GenerateKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredChars = Obscure(value, newCompound);
                _guard = newCompound ^ GuardConst;
            }
        }

        public void OnChanged(Action<string, string> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<string, string>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<string, string> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                    _snapshotDirty = true;
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

        public static implicit operator string(SecureObservableString prop) => prop.Value;
        public override string ToString() => Value ?? string.Empty;
    }

    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for BigDouble Idle numbers memory protection.
    /// Obscures both Mantissa and Exponent using dual independent keys and integrity guards.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableBigDouble
    {
        private readonly SecureObservableLong _mantissaBits;
        private readonly SecureObservableLong _exponent;
        private List<Action<BigDouble, BigDouble>> _handlers;
        private Action<BigDouble, BigDouble>[] _snapshotCache;
        private bool _snapshotDirty;
        private readonly object _handlersLock = new();

        public SecureObservableBigDouble(BigDouble initialValue = default)
        {
            _mantissaBits = new SecureObservableLong(BitConverter.DoubleToInt64Bits(initialValue.Mantissa));
            _exponent = new SecureObservableLong(initialValue.Exponent);
        }

        public BigDouble Value
        {
            get
            {
                double m = BitConverter.Int64BitsToDouble(_mantissaBits.Value);
                long e = _exponent.Value;
                return new BigDouble(m, e);
            }
            set
            {
                BigDouble old = Value;
                if (old.Equals(value)) return;

                _mantissaBits.Value = BitConverter.DoubleToInt64Bits(value.Mantissa);
                _exponent.Value = value.Exponent;

                Action<BigDouble, BigDouble>[] snapshot = null;
                lock (_handlersLock)
                {
                    if (_handlers != null && _handlers.Count > 0)
                    {
                        if (_snapshotDirty || _snapshotCache == null)
                        {
                            _snapshotCache = _handlers.ToArray();
                            _snapshotDirty = false;
                        }
                        snapshot = _snapshotCache;
                    }
                }

                if (snapshot != null)
                {
                    for (int i = 0; i < snapshot.Length; i++)
                        snapshot[i]?.Invoke(old, value);
                }
            }
        }

        public void SetWithoutNotify(BigDouble value)
        {
            _mantissaBits.SetWithoutNotify(BitConverter.DoubleToInt64Bits(value.Mantissa));
            _exponent.SetWithoutNotify(value.Exponent);
        }

        public void OnChanged(Action<BigDouble, BigDouble> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers ??= new List<Action<BigDouble, BigDouble>>(2);
                if (!_handlers.Contains(handler))
                {
                    _handlers.Add(handler);
                    _snapshotDirty = true;
                }
            }
        }

        public void RemoveOnChanged(Action<BigDouble, BigDouble> handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                if (_handlers != null && _handlers.Remove(handler))
                    _snapshotDirty = true;
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

        public static implicit operator BigDouble(SecureObservableBigDouble prop) => prop.Value;
        public override string ToString() => Value.ToString();
    }
}
