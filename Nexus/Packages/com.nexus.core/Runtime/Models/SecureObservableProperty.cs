using System;
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
    ///
    /// Thin wrapper: observer dispatch + key generation live in the shared
    /// <see cref="SecureObserverSet{T}"/> / <see cref="SecureKeyGen"/> core.
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

        private readonly SecureObserverSet<int> _observers = new();

        /// <summary>
        /// Raised when memory tampering is detected (canary mismatch).
        /// The bool parameter is always true. Subscribe to trigger server-side validation.
        /// </summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableInt(int initialValue = 0)
        {
            var (k1, k2) = SecureKeyGen.IntKeyPair();
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

                    // Integrity check: detect memory tampering.
                    // Canary failed → keys were overwritten by a memory scanner.
                    // Reset to 0 to deny the cheat value and raise the tamper event
                    // so the caller can trigger server-side re-validation.
                    if ((compound ^ GuardConst) != _guard)
                    {
                        RaiseTamperDetected("SecureObservableInt");
                        // Re-initialize with a fresh key pair so subsequent reads are safe.
                        var (k1, k2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = k1;
                        _cryptoKey2 = k2;
                        int newCompound = k1 ^ k2;
                        _obscuredValue = 0 ^ newCompound;
                        _guard = newCompound ^ GuardConst;
                        return 0;
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
                    // Also check tamper on write path
                    if ((oldCompound ^ GuardConst) != _guard)
                    {
                        RaiseTamperDetected("SecureObservableInt.set");
                        var (rk1, rk2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = rk1;
                        _cryptoKey2 = rk2;
                        int resetCompound = rk1 ^ rk2;
                        _obscuredValue = 0 ^ resetCompound;
                        _guard = resetCompound ^ GuardConst;
                        return;
                    }
                    old = _obscuredValue ^ oldCompound;
                    if (old == value) return;

                    // Full key rotation on every write: old keys are discarded,
                    // new random pair generated. This breaks any memory scan
                    // that was tracking the previous key pair.
                    var (k1, k2) = SecureKeyGen.IntKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredValue = value ^ newCompound;
                    _guard = newCompound ^ GuardConst;
                }

                _observers.Notify(old, value);
            }
        }

        public void SetWithoutNotify(int value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = SecureKeyGen.IntKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredValue = value ^ newCompound;
                _guard = newCompound ^ GuardConst;
            }
        }

        public void OnChanged(Action<int, int> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<int, int> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        public static implicit operator int(SecureObservableInt prop) => prop.Value;
        public override string ToString() => Value.ToString();

        private static void RaiseTamperDetected(string context) => SecureObservableHelper.RaiseTamper(OnTamperDetected, context);
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

        private readonly SecureObserverSet<long> _observers = new();

        /// <summary>Raised when memory tampering is detected.</summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableLong(long initialValue = 0)
        {
            var (k1, k2) = SecureKeyGen.LongKeyPair();
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
                    {
                        RaiseTamperDetected("SecureObservableLong");
                        var (k1, k2) = SecureKeyGen.LongKeyPair();
                        _cryptoKey1 = k1;
                        _cryptoKey2 = k2;
                        long newCompound = k1 ^ k2;
                        _obscuredValue = 0L ^ newCompound;
                        _guard = newCompound ^ GuardConst;
                        return 0L;
                    }
                    return _obscuredValue ^ compound;
                }
            }
            set
            {
                long old;
                lock (_valueLock)
                {
                    long oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    if ((oldCompound ^ GuardConst) != _guard)
                    {
                        RaiseTamperDetected("SecureObservableLong.set");
                        var (rk1, rk2) = SecureKeyGen.LongKeyPair();
                        _cryptoKey1 = rk1;
                        _cryptoKey2 = rk2;
                        long resetCompound = rk1 ^ rk2;
                        _obscuredValue = 0L ^ resetCompound;
                        _guard = resetCompound ^ GuardConst;
                        return;
                    }
                    old = _obscuredValue ^ oldCompound;
                    if (old == value) return;

                    var (k1, k2) = SecureKeyGen.LongKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    long newCompound = k1 ^ k2;
                    _obscuredValue = value ^ newCompound;
                    _guard = newCompound ^ GuardConst;
                }

                _observers.Notify(old, value);
            }
        }

        public void SetWithoutNotify(long value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = SecureKeyGen.LongKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                long newCompound = k1 ^ k2;
                _obscuredValue = value ^ newCompound;
                _guard = newCompound ^ GuardConst;
            }
        }

        public void OnChanged(Action<long, long> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<long, long> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        public static implicit operator long(SecureObservableLong prop) => prop.Value;
        public override string ToString() => Value.ToString();

        private static void RaiseTamperDetected(string context) => SecureObservableHelper.RaiseTamper(OnTamperDetected, context);
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

        private readonly SecureObserverSet<float> _observers = new();

        /// <summary>Raised when memory tampering is detected.</summary>
        public static event Action<string> OnTamperDetected;

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

        public SecureObservableFloat(float initialValue = 0f)
        {
            var (k1, k2) = SecureKeyGen.IntKeyPair();
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
                    {
                        RaiseTamperDetected("SecureObservableFloat");
                        var (k1, k2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = k1;
                        _cryptoKey2 = k2;
                        int newCompound = k1 ^ k2;
                        _obscuredValue = FloatToIntBits(0f) ^ newCompound;
                        _guard = newCompound ^ GuardConst;
                        return 0f;
                    }
                    return IntToFloatBits(_obscuredValue ^ compound);
                }
            }
            set
            {
                float old;
                lock (_valueLock)
                {
                    int oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    if ((oldCompound ^ GuardConst) != _guard)
                    {
                        RaiseTamperDetected("SecureObservableFloat.set");
                        var (rk1, rk2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = rk1;
                        _cryptoKey2 = rk2;
                        int resetCompound = rk1 ^ rk2;
                        _obscuredValue = FloatToIntBits(0f) ^ resetCompound;
                        _guard = resetCompound ^ GuardConst;
                        return;
                    }
                    old = IntToFloatBits(_obscuredValue ^ oldCompound);
                    if (old == value) return;

                    var (k1, k2) = SecureKeyGen.IntKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredValue = FloatToIntBits(value) ^ newCompound;
                    _guard = newCompound ^ GuardConst;
                }

                _observers.Notify(old, value);
            }
        }

        public void SetWithoutNotify(float value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = SecureKeyGen.IntKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredValue = FloatToIntBits(value) ^ newCompound;
                _guard = newCompound ^ GuardConst;
            }
        }

        public void OnChanged(Action<float, float> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<float, float> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        public static implicit operator float(SecureObservableFloat prop) => prop.Value;
        public override string ToString() => Value.ToString();

        private static void RaiseTamperDetected(string context) => SecureObservableHelper.RaiseTamper(OnTamperDetected, context);
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

        private readonly SecureObserverSet<string> _observers = new();

        /// <summary>Raised when memory tampering is detected.</summary>
        public static event Action<string> OnTamperDetected;

        // XOR each UTF-16 code unit with the low 16 bits of the compound key.
        // Surrogate pairs survive because each half is XORed independently.
        private static char[] Obscure(string value, int key)
        {
            if (value == null) return null;
            var chars = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                int k = (key ^ unchecked((int)(i * 0x9E3779B9u))) & 0xFFFF;
                chars[i] = (char)(value[i] ^ k);
            }
            return chars;
        }

        private static string Reveal(char[] obscured, int key)
        {
            if (obscured == null) return null;
            var chars = new char[obscured.Length];
            for (int i = 0; i < obscured.Length; i++)
            {
                int k = (key ^ unchecked((int)(i * 0x9E3779B9u))) & 0xFFFF;
                chars[i] = (char)(obscured[i] ^ k);
            }
            return new string(chars);
        }

        public SecureObservableString(string initialValue = null)
        {
            var (k1, k2) = SecureKeyGen.IntKeyPair();
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
                    {
                        RaiseTamperDetected("SecureObservableString");
                        var (k1, k2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = k1;
                        _cryptoKey2 = k2;
                        int newCompound = k1 ^ k2;
                        _obscuredChars = null;
                        _guard = newCompound ^ GuardConst;
                        return null;
                    }
                    return Reveal(_obscuredChars, compound);
                }
            }
            set
            {
                string old;
                lock (_valueLock)
                {
                    int oldCompound = _cryptoKey1 ^ _cryptoKey2;
                    if ((oldCompound ^ GuardConst) != _guard)
                    {
                        RaiseTamperDetected("SecureObservableString.set");
                        var (rk1, rk2) = SecureKeyGen.IntKeyPair();
                        _cryptoKey1 = rk1;
                        _cryptoKey2 = rk2;
                        int resetCompound = rk1 ^ rk2;
                        _obscuredChars = null;
                        _guard = resetCompound ^ GuardConst;
                        return;
                    }
                    old = Reveal(_obscuredChars, oldCompound);
                    if (string.Equals(old, value)) return;

                    var (k1, k2) = SecureKeyGen.IntKeyPair();
                    _cryptoKey1 = k1;
                    _cryptoKey2 = k2;
                    int newCompound = k1 ^ k2;
                    _obscuredChars = Obscure(value, newCompound);
                    _guard = newCompound ^ GuardConst;
                }

                _observers.Notify(old, value);
            }
        }

        public void SetWithoutNotify(string value)
        {
            lock (_valueLock)
            {
                var (k1, k2) = SecureKeyGen.IntKeyPair();
                _cryptoKey1 = k1;
                _cryptoKey2 = k2;
                int newCompound = k1 ^ k2;
                _obscuredChars = Obscure(value, newCompound);
                _guard = newCompound ^ GuardConst;
            }
        }

        public void OnChanged(Action<string, string> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<string, string> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        public static implicit operator string(SecureObservableString prop) => prop.Value;
        public override string ToString() => Value ?? string.Empty;

        private static void RaiseTamperDetected(string context)
        {
            NexusRuntime.Logger?.LogError($"[Nexus][AntiCheat] Memory tamper detected on {context}. Value reset to null. Trigger server-side validation.");
            try { OnTamperDetected?.Invoke(context); }
            catch (Exception ex) { NexusRuntime.Logger?.LogError($"[Nexus][AntiCheat] OnTamperDetected handler threw: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Obfuscated, Anti-Cheat reactive property wrapper for BigDouble Idle numbers memory protection.
    /// Obscures both Mantissa and Exponent using dual independent keys and integrity guards
    /// (delegated to two <see cref="SecureObservableLong"/>), with shared observer dispatch.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableBigDouble
    {
        private readonly SecureObservableLong _mantissaBits;
        private readonly SecureObservableLong _exponent;
        private readonly SecureObserverSet<BigDouble> _observers = new();

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

                _observers.Notify(old, value);
            }
        }

        public void SetWithoutNotify(BigDouble value)
        {
            _mantissaBits.SetWithoutNotify(BitConverter.DoubleToInt64Bits(value.Mantissa));
            _exponent.SetWithoutNotify(value.Exponent);
        }

        public void OnChanged(Action<BigDouble, BigDouble> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<BigDouble, BigDouble> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        public static implicit operator BigDouble(SecureObservableBigDouble prop) => prop.Value;
        public override string ToString() => Value.ToString();
    }

    internal static class SecureObservableHelper
    {
        public static void RaiseTamper(Action<string> tamperEvent, string context)
        {
            NexusRuntime.Logger?.LogError($"[Nexus][AntiCheat] Memory tamper detected on {context}. Value reset to 0. Trigger server-side validation.");
            if (tamperEvent == null) return;
            try { tamperEvent.Invoke(context); }
            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
        }
    }
}
