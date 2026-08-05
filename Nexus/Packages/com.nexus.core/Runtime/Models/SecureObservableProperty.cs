using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Obfuscated reactive property wrapper for an int, with tamper DETECTION.
    ///
    /// Storage scheme (lock-guarded):
    ///   _obscuredValue = value ^ (_cryptoKey1 ^ _cryptoKey2)
    ///   _guard = (_cryptoKey1 ^ _cryptoKey2) ^ GUARD_CONSTANT
    /// plus a redundant copy of the same value under a third, independent key, which is the
    /// repair source when the primary canary no longer matches.
    ///
    /// What this actually provides:
    ///   • naive single-field value scans ("find 500, write 999") find nothing, because no
    ///     field ever holds the plaintext;
    ///   • keys are rotated on every write, so a value frozen/pinned in RAM stops matching
    ///     its canary and the freeze attempt is detected on the next access;
    ///   • a detected mismatch raises <see cref="OnTamperDetected"/> and
    ///     <see cref="TamperDetected"/> so the game can force server-side re-validation.
    ///
    /// What this is NOT: a security boundary. The keys and the canary live in the SAME object
    /// next to the obscured value, so any tool that can READ process memory (GameGuardian,
    /// CheatEngine, a debugger, a dumped heap) can read key1, key2 and guard and compute the
    /// plaintext — and an edit of the obscured payload that leaves the key trio untouched is
    /// not detected at all. This is obfuscation plus tamper detection, not encryption: every
    /// value that matters must still be validated server-side.
    ///
    /// Thin wrapper: the whole seal/unseal/canary/repair/dispatch protocol lives in the shared
    /// <see cref="SecureObservableCore{T}"/> core.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableInt
    {
        private static readonly Func<Action<string>> StaticTamperAccessor = () => OnTamperDetected;

        private readonly SecureIntCore _core;

        /// <summary>
        /// Raised when memory tampering is detected (canary mismatch). The argument carries
        /// the property context — type name, optional debug name and access path.
        /// Subscribe to trigger server-side validation.
        /// Static, therefore global: prefer the per-instance <see cref="TamperDetected"/>
        /// when you only care about one property.
        /// </summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableInt(int initialValue = 0, string debugName = null)
        {
            _core = new SecureIntCore(initialValue, debugName, StaticTamperAccessor);
        }

        /// <summary>Optional diagnostic name supplied at construction; null when not supplied.</summary>
        public string DebugName => _core.DebugName;

        /// <summary>
        /// Per-instance tamper notification, raised alongside <see cref="OnTamperDetected"/>.
        /// Lets consumers observe tampering without subscribing to global static state.
        /// </summary>
        public event Action<string> TamperDetected
        {
            add => _core.TamperDetected += value;
            remove => _core.TamperDetected -= value;
        }

        public int Value
        {
            get => _core.ReadValue();
            set => _core.WriteValue(value);
        }

        /// <summary>
        /// Set the underlying value without firing OnChanged. By default this method
        /// preserves the integrity canary. Callers may pass validateCanary=false only
        /// when they absolutely control the value source (internal-only scenarios).
        /// </summary>
        public void SetWithoutNotify(int value, bool validateCanary = true) => _core.WriteWithoutNotify(value, validateCanary);

        public void OnChanged(Action<int, int> handler) => _core.OnChanged(handler);
        public void RemoveOnChanged(Action<int, int> handler) => _core.RemoveOnChanged(handler);
        public void ClearOnChanged() => _core.ClearOnChanged();

        public static implicit operator int(SecureObservableInt prop) => prop.Value;
        public override string ToString() => Value.ToString();

        /// <summary>
        /// Clears static tamper event subscribers. Only allowed from within the declaring type.
        /// Used by NexusRuntime.Reset to avoid leaked handlers across domain reloads.
        /// </summary>
        public static void ClearOnTamperDetected() => OnTamperDetected = null;
    }

    /// <summary>
    /// Obfuscated reactive property wrapper for a 64-bit integer, with tamper detection.
    /// Same dual-key XOR storage, redundant copy, canary and key rotation as
    /// <see cref="SecureObservableInt"/> — and the same non-guarantees: the keys sit next to
    /// the obscured value, so this defeats naive value scans and freezes, not an attacker who
    /// can read process memory. Server-side validation remains required.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableLong
    {
        private static readonly Func<Action<string>> StaticTamperAccessor = () => OnTamperDetected;

        private readonly SecureLongCore _core;

        /// <summary>Raised when memory tampering is detected (canary mismatch). Global —
        /// prefer <see cref="TamperDetected"/> for a single property.</summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableLong(long initialValue = 0, string debugName = null)
        {
            _core = new SecureLongCore(initialValue, debugName, StaticTamperAccessor);
        }

        /// <summary>Optional diagnostic name supplied at construction; null when not supplied.</summary>
        public string DebugName => _core.DebugName;

        /// <summary>Per-instance tamper notification, raised alongside <see cref="OnTamperDetected"/>.</summary>
        public event Action<string> TamperDetected
        {
            add => _core.TamperDetected += value;
            remove => _core.TamperDetected -= value;
        }

        public long Value
        {
            get => _core.ReadValue();
            set => _core.WriteValue(value);
        }

        /// <summary>
        /// Set the underlying value without firing OnChanged. By default this method
        /// preserves the integrity canary. Callers may pass validateCanary=false only
        /// when they absolutely control the value source (internal-only scenarios).
        /// </summary>
        public void SetWithoutNotify(long value, bool validateCanary = true) => _core.WriteWithoutNotify(value, validateCanary);

        public void OnChanged(Action<long, long> handler) => _core.OnChanged(handler);
        public void RemoveOnChanged(Action<long, long> handler) => _core.RemoveOnChanged(handler);
        public void ClearOnChanged() => _core.ClearOnChanged();

        public static implicit operator long(SecureObservableLong prop) => prop.Value;
        public override string ToString() => Value.ToString();

        /// <summary>
        /// Clears static tamper event subscribers for SecureObservableLong.
        /// Used by NexusRuntime.Reset to avoid leaked handlers across domain reloads.
        /// </summary>
        public static void ClearOnTamperDetected() => OnTamperDetected = null;
    }

    /// <summary>
    /// Obfuscated reactive property wrapper for a float, with tamper detection.
    /// The IEEE-754 bit pattern is what gets XOR-obscured (dual keys + canary + redundant
    /// copy), so sign and exponent bits round-trip untouched. Same guarantees — and the same
    /// non-guarantees — as <see cref="SecureObservableInt"/>: obfuscation against naive value
    /// scans and freeze attempts plus tamper detection, NOT protection against an attacker who
    /// can read process memory. Server-side validation remains required.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableFloat
    {
        private static readonly Func<Action<string>> StaticTamperAccessor = () => OnTamperDetected;

        private readonly SecureFloatCore _core;

        /// <summary>Raised when memory tampering is detected (canary mismatch). Global —
        /// prefer <see cref="TamperDetected"/> for a single property.</summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableFloat(float initialValue = 0f, string debugName = null)
        {
            _core = new SecureFloatCore(initialValue, debugName, StaticTamperAccessor);
        }

        /// <summary>Optional diagnostic name supplied at construction; null when not supplied.</summary>
        public string DebugName => _core.DebugName;

        /// <summary>Per-instance tamper notification, raised alongside <see cref="OnTamperDetected"/>.</summary>
        public event Action<string> TamperDetected
        {
            add => _core.TamperDetected += value;
            remove => _core.TamperDetected -= value;
        }

        public float Value
        {
            get => _core.ReadValue();
            set => _core.WriteValue(value);
        }

        /// <summary>
        /// Set the underlying value without firing OnChanged. By default this method
        /// preserves the integrity canary. Callers may pass validateCanary=false only
        /// when they absolutely control the value source (internal-only scenarios).
        /// </summary>
        public void SetWithoutNotify(float value, bool validateCanary = true) => _core.WriteWithoutNotify(value, validateCanary);

        public void OnChanged(Action<float, float> handler) => _core.OnChanged(handler);
        public void RemoveOnChanged(Action<float, float> handler) => _core.RemoveOnChanged(handler);
        public void ClearOnChanged() => _core.ClearOnChanged();

        public static implicit operator float(SecureObservableFloat prop) => prop.Value;
        public override string ToString() => Value.ToString();

        /// <summary>
        /// Clears static tamper event subscribers for SecureObservableFloat.
        /// Used by NexusRuntime.Reset to avoid leaked handlers across domain reloads.
        /// </summary>
        public static void ClearOnTamperDetected() => OnTamperDetected = null;
    }

    /// <summary>
    /// Obfuscated reactive property wrapper for a string, with tamper detection.
    /// The string is stored XOR-masked per UTF-16 code unit under a dual-key compound key,
    /// with an integrity canary and a redundant copy under a third key. Same guarantees — and
    /// the same non-guarantees — as <see cref="SecureObservableInt"/>: it defeats naive value
    /// scans and freeze attempts and detects canary tampering, but the keys live next to the
    /// masked buffer, so it is not a security boundary. Server-side validation remains required.
    ///
    /// NOTE: reading <see cref="Value"/> reconstructs the string — allocation is inherent
    /// to strings, so this is NOT 0-GC (acceptable for low-frequency data).
    /// </summary>
    [Preserve]
    public sealed class SecureObservableString
    {
        private static readonly Func<Action<string>> StaticTamperAccessor = () => OnTamperDetected;

        private readonly SecureStringCore _core;

        /// <summary>Raised when memory tampering is detected (canary mismatch). Global —
        /// prefer <see cref="TamperDetected"/> for a single property.</summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableString(string initialValue = null, string debugName = null)
        {
            _core = new SecureStringCore(initialValue, debugName, StaticTamperAccessor);
        }

        /// <summary>Optional diagnostic name supplied at construction; null when not supplied.</summary>
        public string DebugName => _core.DebugName;

        /// <summary>Per-instance tamper notification, raised alongside <see cref="OnTamperDetected"/>.</summary>
        public event Action<string> TamperDetected
        {
            add => _core.TamperDetected += value;
            remove => _core.TamperDetected -= value;
        }

        public string Value
        {
            get => _core.ReadValue();
            set => _core.WriteValue(value);
        }

        /// <summary>
        /// Set the underlying value without firing OnChanged. By default this method
        /// preserves the integrity canary. Callers may pass validateCanary=false only
        /// when they absolutely control the value source (internal-only scenarios).
        /// </summary>
        public void SetWithoutNotify(string value, bool validateCanary = true) => _core.WriteWithoutNotify(value, validateCanary);

        public void OnChanged(Action<string, string> handler) => _core.OnChanged(handler);
        public void RemoveOnChanged(Action<string, string> handler) => _core.RemoveOnChanged(handler);
        public void ClearOnChanged() => _core.ClearOnChanged();

        public static implicit operator string(SecureObservableString prop) => prop.Value;
        public override string ToString() => Value ?? string.Empty;

        /// <summary>
        /// Clears static tamper event subscribers for SecureObservableString.
        /// Used by NexusRuntime.Reset to avoid leaked handlers across domain reloads.
        /// </summary>
        public static void ClearOnTamperDetected() => OnTamperDetected = null;
    }

    /// <summary>
    /// Obfuscated reactive property wrapper for BigDouble idle numbers, with tamper detection.
    /// Mantissa bit pattern and exponent are obscured together under one dual-key set, with an
    /// integrity canary, a redundant copy under a third key and key rotation on every write —
    /// and, because both words share one lock, the composite pair is never observed
    /// half-updated. Same guarantees, and the same non-guarantees, as
    /// <see cref="SecureObservableInt"/>: obfuscation against naive value scans and freeze
    /// attempts plus tamper detection — not a security boundary against an attacker who can
    /// read process memory. Server-side validation remains required.
    /// </summary>
    [Preserve]
    public sealed class SecureObservableBigDouble
    {
        private static readonly Func<Action<string>> StaticTamperAccessor = () => OnTamperDetected;

        private readonly SecureBigDoubleCore _core;

        /// <summary>
        /// Raised when memory tampering is detected (canary mismatch). Global — prefer
        /// <see cref="TamperDetected"/> for a single property. Previously a tampered
        /// BigDouble surfaced on <see cref="SecureObservableLong.OnTamperDetected"/>, because
        /// the value was split across two inner SecureObservableLong instances; it now reports
        /// under its own type.
        /// </summary>
        public static event Action<string> OnTamperDetected;

        public SecureObservableBigDouble(BigDouble initialValue = default, string debugName = null)
        {
            _core = new SecureBigDoubleCore(initialValue, debugName, StaticTamperAccessor);
        }

        /// <summary>Optional diagnostic name supplied at construction; null when not supplied.</summary>
        public string DebugName => _core.DebugName;

        /// <summary>Per-instance tamper notification, raised alongside <see cref="OnTamperDetected"/>.</summary>
        public event Action<string> TamperDetected
        {
            add => _core.TamperDetected += value;
            remove => _core.TamperDetected -= value;
        }

        public BigDouble Value
        {
            get => _core.ReadValue();
            set => _core.WriteValue(value);
        }

        /// <summary>
        /// Set the composite BigDouble value without firing notifications. By default the
        /// integrity canary is validated after the write; callers may pass validateCanary=false
        /// to skip that validation for internal scenarios.
        /// </summary>
        public void SetWithoutNotify(BigDouble value, bool validateCanary = true) => _core.WriteWithoutNotify(value, validateCanary);

        public void OnChanged(Action<BigDouble, BigDouble> handler) => _core.OnChanged(handler);
        public void RemoveOnChanged(Action<BigDouble, BigDouble> handler) => _core.RemoveOnChanged(handler);
        public void ClearOnChanged() => _core.ClearOnChanged();

        public static implicit operator BigDouble(SecureObservableBigDouble prop) => prop.Value;
        public override string ToString() => Value.ToString();

        /// <summary>
        /// Clears static tamper event subscribers for SecureObservableBigDouble.
        /// Mirrors the other family members so NexusRuntime.Reset can avoid leaked handlers
        /// across domain reloads.
        /// </summary>
        public static void ClearOnTamperDetected() => OnTamperDetected = null;
    }

    internal static class SecureObservableHelper
    {
        /// <summary>
        /// Single tamper reporting path for the whole family: logs the incident, then raises the
        /// owning type's static event and the per-instance event. Handler exceptions are
        /// isolated (and logged with their stack trace) so a bad subscriber cannot break the
        /// caller's repair path.
        /// </summary>
        public static void RaiseTamper(Action<string> staticEvent, Action<string> instanceEvent, string context, string resolution)
        {
            NexusRuntime.Logger?.LogError($"[Nexus][AntiCheat] Memory tamper detected on {context}. {resolution} Trigger server-side validation.");
            InvokeTamperHandler(staticEvent, context);
            InvokeTamperHandler(instanceEvent, context);
        }

        /// <summary>Invokes a tamper handler chain, isolating and logging any exception.</summary>
        public static void InvokeTamperHandler(Action<string> handler, string context)
        {
            if (handler == null) return;
            try { handler.Invoke(context); }
            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
        }
    }
}
