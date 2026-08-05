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
        // The snapshot is now published as a VOLATILE array reference and
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
    /// <see cref="SnapshotDelegateSet{TDelegate}"/> core plus a <see cref="Notify(T,T)"/> that
    /// matches the family's (old, new) change signature. <see cref="GetSnapshot"/> lets
    /// callers drive their own dispatch loop (e.g. ObservableProperty's reentrancy coalescing).
    /// </summary>
    internal sealed class SecureObserverSet<T>
    {
        private readonly SnapshotDelegateSet<Action<T, T>> _delegates = new();

        // Reentrancy coalescing. A handler that writes the observable's Value
        // re-enters Notify; without a guard this recursed unboundedly (stack overflow).
        // The claim-or-queue protocol mirrors ObservableProperty<T>: exactly one thread
        // becomes the dispatcher, everyone else (including same-thread reentrant writes)
        // coalesces into the pending slot, and the dispatcher drains pending changes in
        // one loop. Dispatch runs OUTSIDE the lock, so handlers never execute under it.
        private readonly object _notifyLock = new();
        private bool _isNotifying; // guarded by _notifyLock
        private bool _hasPending;   // guarded by _notifyLock
        private T _pendingOld;      // guarded by _notifyLock
        private T _pendingNew;      // guarded by _notifyLock
        private long _dispatchedSequence; // guarded by _notifyLock

        public void OnChanged(Action<T, T> handler) => _delegates.Add(handler);
        public void RemoveOnChanged(Action<T, T> handler) => _delegates.Remove(handler);
        public void Clear() => _delegates.Clear();

        /// <summary>Returns the current handler snapshot (zero-GC cached).</summary>
        public Action<T, T>[] GetSnapshot() => _delegates.GetSnapshot();

        /// <summary>Invokes the snapshot cache with the change. Zero-GC on the hot path:
        /// the array is rebuilt only when the handler set changed since the last notify.
        /// Reentrancy-safe: a handler that writes the value is coalesced, not recursed.</summary>
        public void Notify(T oldValue, T newValue) => Notify(oldValue, newValue, 0L);

        /// <summary>
        /// Sequenced dispatch. <paramref name="sequence"/> is the commit ticket stamped under
        /// the owner's value lock (see <see cref="SecureCommitSequence"/>); a change older than
        /// the newest ticket already accepted here is DROPPED, so the last committed value is
        /// always the last one observed even when two writers commit concurrently and reach
        /// this method in the opposite order.
        ///
        /// The staleness check deliberately shares the critical section with the
        /// claim-or-queue decision below: as a separate lock it could not stop a stale change
        /// from overwriting the pending slot of a newer one. Handler invocation still happens
        /// outside the lock. Pass 0 to opt out (ordering then follows call order).
        /// </summary>
        public void Notify(T oldValue, T newValue, long sequence)
        {
            lock (_notifyLock)
            {
                if (sequence != 0L)
                {
                    if (sequence <= _dispatchedSequence) return;
                    _dispatchedSequence = sequence;
                }

                if (_isNotifying)
                {
                    if (!_hasPending)
                    {
                        _pendingOld = oldValue;
                    }
                    _pendingNew = newValue;
                    _hasPending = true;
                    return;
                }
                _isNotifying = true;
            }

            bool completedNormally = false;
            try
            {
                while (true)
                {
                    Action<T, T>[] snapshot = _delegates.GetSnapshot();
                    if (snapshot != null)
                    {
                        for (int i = 0; i < snapshot.Length; i++)
                            snapshot[i]?.Invoke(oldValue, newValue);
                    }

                    // Exit decision + guard clear are ONE critical section: a writer that
                    // queues after this point observes _isNotifying == false and becomes
                    // the dispatcher itself — the handoff cannot drop a change.
                    T pOld, pNew;
                    bool hasPending;
                    lock (_notifyLock)
                    {
                        hasPending = _hasPending;
                        if (hasPending)
                        {
                            pOld = _pendingOld;
                            pNew = _pendingNew;
                            _hasPending = false;
                        }
                        else
                        {
                            _isNotifying = false;
                            pOld = default;
                            pNew = default;
                        }
                    }
                    if (!hasPending) break;
                    oldValue = pOld;
                    newValue = pNew;
                }
                completedNormally = true;
            }
            finally
            {
                // Defensive: an exception escaping a handler must not leave the guard
                // claimed forever. Cleared ONLY on the exception path — after the loop's
                // normal exit another thread may already hold the dispatcher role, and an
                // unconditional reset here would drop its queued change and permit a
                // second concurrent dispatcher.
                if (!completedNormally)
                {
                    lock (_notifyLock)
                    {
                        _isNotifying = false;
                        _hasPending = false;
                    }
                }
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

        // Scratch buffers are THREAD-STATIC rather than shared under a global lock: keys
        // are rotated on every value write (not only at construction), so a single static
        // lock serialized every secure write across the whole game. RandomNumberGenerator
        // is thread-safe, so per-thread buffers keep the path allocation-free AND
        // contention-free.
        [ThreadStatic] private static byte[] s_intBuf;
        [ThreadStatic] private static byte[] s_longBuf;

        public static int NextIntKey()
        {
            var buf = s_intBuf ??= new byte[4];
            s_rng.GetBytes(buf);
            // Full 32-bit range: masking the sign bit (the previous & 0x7FFFFFFF) left the
            // stored value's sign bit unmasked, so a memory scanner could filter on it.
            // Only zero is rejected, to avoid a degenerate XOR (key ^ 0 == key).
            int key = BitConverter.ToInt32(buf, 0);
            return key != 0 ? key : 0x4E5855; // fallback "NXU"
        }

        public static long NextLongKey()
        {
            var buf = s_longBuf ??= new byte[8];
            s_rng.GetBytes(buf);
            long key = BitConverter.ToInt64(buf, 0);
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

    /// <summary>
    /// How the SecureObservable family reacts to a detected integrity (canary) mismatch.
    /// </summary>
    public enum SecureTamperResponse
    {
        /// <summary>Restore the type's default value (0 / null / BigDouble.Zero).</summary>
        ResetToDefault,

        /// <summary>
        /// Restore the last known good value from the redundant copy, falling back to the
        /// default only when that copy fails its own integrity check. This is the default:
        /// an unconditional reset is weaponizable — an attacker who deliberately corrupts a
        /// canary would otherwise be able to zero the player's currency.
        /// </summary>
        KeepLastKnownGood
    }

    /// <summary>
    /// Process-wide policy for the SecureObservable family.
    /// </summary>
    public static class SecureObservableSettings
    {
        private static volatile SecureTamperResponse s_tamperResponse = SecureTamperResponse.KeepLastKnownGood;

        /// <summary>
        /// Response applied when a canary mismatch is detected. Defaults to
        /// <see cref="SecureTamperResponse.KeepLastKnownGood"/>. The tamper events fire
        /// regardless of the setting.
        /// </summary>
        public static SecureTamperResponse TamperResponse
        {
            get => s_tamperResponse;
            set => s_tamperResponse = value;
        }
    }

    /// <summary>
    /// Monotonic commit ticket generator. Advanced under the owner's value lock so ticket
    /// order equals commit order, then handed to
    /// <see cref="SecureObserverSet{T}.Notify(T,T,long)"/> outside the lock.
    /// </summary>
    internal struct SecureCommitSequence
    {
        private long _sequence;

        /// <summary>Stamps the next commit. The caller MUST hold the owner's value lock.</summary>
        public long Next() => ++_sequence;
    }

    /// <summary>
    /// Shared protocol for the whole SecureObservable family: the value lock, the
    /// canary-checked read/write paths, the tamper response policy (repair from the redundant
    /// copy), key rotation on every write, observer dispatch and commit sequencing.
    /// The public wrappers hold one of these and forward to it, so the protocol — previously
    /// copy-pasted five times — exists exactly once.
    ///
    /// Storage stays with the subclass because the encodings differ (32-bit word, 64-bit word,
    /// per-char masked buffer). A subclass keeps TWO independent copies of the value: the
    /// primary (dual-key XOR + canary) and a redundant one under a third key, and reports
    /// per-copy canary validity so a mismatch can be repaired instead of reset.
    /// </summary>
    internal abstract class SecureObservableCore<T>
    {
        private const string RepairedResolution = "Value restored from the redundant copy.";
        private const string ResetResolution = "Value reset to the default.";
        private const string ValidationResolution = "Seal round-trip validation failed.";

        private readonly object _valueLock = new();
        private readonly SecureObserverSet<T> _observers = new();
        private readonly Func<Action<string>> _staticTamperAccessor;
        private readonly string _context;
        private SecureCommitSequence _commitSequence; // guarded by _valueLock

        protected SecureObservableCore(string typeName, string debugName, Func<Action<string>> staticTamperAccessor)
        {
            DebugName = debugName;
            _context = string.IsNullOrEmpty(debugName) ? typeName : $"{typeName}[{debugName}]";
            _staticTamperAccessor = staticTamperAccessor;
        }

        /// <summary>Optional diagnostic name of the owning property; null when not supplied.</summary>
        public string DebugName { get; }

        /// <summary>Per-instance tamper notification, raised alongside the owner's static event.</summary>
        public event Action<string> TamperDetected;

        // ── Type-specific storage protocol ──

        protected abstract T DefaultValue { get; }
        protected abstract bool ValuesEqual(T left, T right);
        /// <summary>Rotates the key set and (re)seals BOTH the primary and the redundant copy.</summary>
        protected abstract void SealValue(T value);
        /// <summary>Unseals the primary copy; false when its canary does not match.</summary>
        protected abstract bool TryUnsealPrimary(out T value);
        /// <summary>Unseals the redundant copy; false when its own canary does not match.</summary>
        protected abstract bool TryUnsealRedundant(out T value);
        /// <summary>Round-trip check for <see cref="WriteWithoutNotify"/>'s optional validation.</summary>
        protected abstract bool VerifySeal(T expected);

        /// <summary>Seals the initial value. Invoked from the leaf constructor so the
        /// subclass fields it touches are already initialized.</summary>
        protected void Initialize(T value) => SealValue(value);

        public T ReadValue()
        {
            T value;
            bool repaired;
            lock (_valueLock)
            {
                if (TryUnsealPrimary(out value)) return value;
                value = ResolveTamperLocked(out repaired);
            }

            // Raised after the lock is released: a tamper handler typically reads the
            // property or calls out to the server, so it must never run under the lock.
            RaiseTamper(_context, repaired ? RepairedResolution : ResetResolution);
            return value;
        }

        public void WriteValue(T value)
        {
            T old;
            long sequence = 0L;
            bool tampered = false;
            bool repaired = false;
            lock (_valueLock)
            {
                if (!TryUnsealPrimary(out old))
                {
                    ResolveTamperLocked(out repaired);
                    tampered = true;
                }
                else if (!ValuesEqual(old, value))
                {
                    SealValue(value);
                    sequence = _commitSequence.Next();
                }
            }

            if (tampered)
            {
                // The incoming write is dropped (as in the original write path): the state is
                // untrusted until the caller re-validates server-side.
                RaiseTamper(_context + ".set", repaired ? RepairedResolution : ResetResolution);
                return;
            }

            if (sequence != 0L) _observers.Notify(old, value, sequence);
        }

        public void WriteWithoutNotify(T value, bool validateCanary)
        {
            bool validationFailed;
            lock (_valueLock)
            {
                SealValue(value);
                validationFailed = validateCanary && !VerifySeal(value);
            }

            if (validationFailed) RaiseTamper(_context + ".SetWithoutNotify.validation", ValidationResolution);
        }

        public void OnChanged(Action<T, T> handler) => _observers.OnChanged(handler);
        public void RemoveOnChanged(Action<T, T> handler) => _observers.RemoveOnChanged(handler);
        public void ClearOnChanged() => _observers.Clear();

        /// <summary>
        /// Canary mismatch handling; caller holds the value lock. The redundant copy is the
        /// repair source whenever its own canary is intact, so corrupting a canary can no
        /// longer wipe the value. Both copies are re-sealed with a fresh key set either way,
        /// so subsequent reads are consistent again.
        /// </summary>
        private T ResolveTamperLocked(out bool repaired)
        {
            T resolved;
            if (SecureObservableSettings.TamperResponse == SecureTamperResponse.KeepLastKnownGood
                && TryUnsealRedundant(out T lastKnownGood))
            {
                resolved = lastKnownGood;
                repaired = true;
            }
            else
            {
                resolved = DefaultValue;
                repaired = false;
            }

            SealValue(resolved);
            return resolved;
        }

        private void RaiseTamper(string context, string resolution)
            => SecureObservableHelper.RaiseTamper(_staticTamperAccessor?.Invoke(), TamperDetected, context, resolution);
    }

    /// <summary>
    /// Storage for family members whose payload fits a 32-bit word (int, float bit pattern).
    /// Primary: <c>value ^ (key1 ^ key2)</c> with canary <c>(key1 ^ key2) ^ GuardConst</c>.
    /// Redundant: <c>value ^ key3</c> with its own canary <c>key3 ^ ShadowGuardConst</c>.
    /// </summary>
    internal abstract class SecureWord32Core<T> : SecureObservableCore<T>
    {
        private const int GuardConst = unchecked((int)0x4E455855);        // "NEXU"
        private const int ShadowGuardConst = unchecked((int)0x53484457);  // "SHDW"

        private int _obscuredValue;
        private int _cryptoKey1;
        private int _cryptoKey2;
        private int _guard;
        private int _shadowValue;
        private int _shadowKey;
        private int _shadowGuard;

        protected SecureWord32Core(string typeName, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(typeName, debugName, staticTamperAccessor) { }

        protected abstract int Encode(T value);
        protected abstract T Decode(int bits);

        protected override void SealValue(T value)
        {
            int bits = Encode(value);

            var (k1, k2) = SecureKeyGen.IntKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            int compound = k1 ^ k2;
            _obscuredValue = bits ^ compound;
            _guard = compound ^ GuardConst;

            // The third key must differ from the compound key, otherwise both copies would
            // hold the same word — exactly the repeated pattern a memory scanner looks for.
            int k3;
            do { k3 = SecureKeyGen.NextIntKey(); } while (k3 == compound);
            _shadowKey = k3;
            _shadowValue = bits ^ k3;
            _shadowGuard = k3 ^ ShadowGuardConst;
        }

        protected override bool TryUnsealPrimary(out T value)
        {
            int compound = _cryptoKey1 ^ _cryptoKey2;
            if ((compound ^ GuardConst) != _guard)
            {
                value = default;
                return false;
            }
            value = Decode(_obscuredValue ^ compound);
            return true;
        }

        protected override bool TryUnsealRedundant(out T value)
        {
            int k3 = _shadowKey;
            if ((k3 ^ ShadowGuardConst) != _shadowGuard)
            {
                value = default;
                return false;
            }
            value = Decode(_shadowValue ^ k3);
            return true;
        }

        protected override bool VerifySeal(T expected)
        {
            int compound = _cryptoKey1 ^ _cryptoKey2;
            if ((_guard ^ GuardConst) != compound) return false;
            if (!ValuesEqual(Decode(_obscuredValue ^ compound), expected)) return false;

            int k3 = _shadowKey;
            if ((_shadowGuard ^ ShadowGuardConst) != k3) return false;
            return ValuesEqual(Decode(_shadowValue ^ k3), expected);
        }
    }

    /// <summary>Int storage core behind <see cref="SecureObservableInt"/>.</summary>
    internal sealed class SecureIntCore : SecureWord32Core<int>
    {
        public SecureIntCore(int initialValue, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(nameof(SecureObservableInt), debugName, staticTamperAccessor)
        {
            Initialize(initialValue);
        }

        protected override int DefaultValue => 0;
        protected override bool ValuesEqual(int left, int right) => left == right;
        protected override int Encode(int value) => value;
        protected override int Decode(int bits) => bits;
    }

    /// <summary>
    /// Float storage core behind <see cref="SecureObservableFloat"/>: the IEEE-754 bit
    /// pattern is what gets obscured.
    ///
    /// Zero-allocation float ↔ int re-interpretation via an explicit-layout union.
    /// Alternative: <c>Unsafe.As&lt;float, int&gt;(ref value)</c>
    /// (System.Runtime.CompilerServices.Unsafe) requires an unsafe context; the union
    /// approach is CLS-compliant and allocation-free.
    /// </summary>
    internal sealed class SecureFloatCore : SecureWord32Core<float>
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBitsUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float AsFloat;
            [System.Runtime.InteropServices.FieldOffset(0)] public int AsInt;
        }

        public SecureFloatCore(float initialValue, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(nameof(SecureObservableFloat), debugName, staticTamperAccessor)
        {
            Initialize(initialValue);
        }

        protected override float DefaultValue => 0f;
        protected override bool ValuesEqual(float left, float right) => left == right;

        protected override int Encode(float value)
        {
            var u = new FloatBitsUnion { AsFloat = value };
            return u.AsInt;
        }

        protected override float Decode(int bits)
        {
            var u = new FloatBitsUnion { AsInt = bits };
            return u.AsFloat;
        }
    }

    /// <summary>
    /// Long storage core behind <see cref="SecureObservableLong"/>. Same two-copy scheme as
    /// <see cref="SecureWord32Core{T}"/>, widened to 64-bit keys.
    /// </summary>
    internal sealed class SecureLongCore : SecureObservableCore<long>
    {
        private const long GuardConst = 0x4E4558554E455855L;        // "NEXUNEXU"
        private const long ShadowGuardConst = 0x5348445753484457L;  // "SHDWSHDW"

        private long _obscuredValue;
        private long _cryptoKey1;
        private long _cryptoKey2;
        private long _guard;
        private long _shadowValue;
        private long _shadowKey;
        private long _shadowGuard;

        public SecureLongCore(long initialValue, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(nameof(SecureObservableLong), debugName, staticTamperAccessor)
        {
            Initialize(initialValue);
        }

        protected override long DefaultValue => 0L;
        protected override bool ValuesEqual(long left, long right) => left == right;

        protected override void SealValue(long value)
        {
            var (k1, k2) = SecureKeyGen.LongKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            long compound = k1 ^ k2;
            _obscuredValue = value ^ compound;
            _guard = compound ^ GuardConst;

            long k3;
            do { k3 = SecureKeyGen.NextLongKey(); } while (k3 == compound);
            _shadowKey = k3;
            _shadowValue = value ^ k3;
            _shadowGuard = k3 ^ ShadowGuardConst;
        }

        protected override bool TryUnsealPrimary(out long value)
        {
            long compound = _cryptoKey1 ^ _cryptoKey2;
            if ((compound ^ GuardConst) != _guard)
            {
                value = 0L;
                return false;
            }
            value = _obscuredValue ^ compound;
            return true;
        }

        protected override bool TryUnsealRedundant(out long value)
        {
            long k3 = _shadowKey;
            if ((k3 ^ ShadowGuardConst) != _shadowGuard)
            {
                value = 0L;
                return false;
            }
            value = _shadowValue ^ k3;
            return true;
        }

        protected override bool VerifySeal(long expected)
        {
            long compound = _cryptoKey1 ^ _cryptoKey2;
            if ((_guard ^ GuardConst) != compound) return false;
            if ((_obscuredValue ^ compound) != expected) return false;

            long k3 = _shadowKey;
            if ((_shadowGuard ^ ShadowGuardConst) != k3) return false;
            return (_shadowValue ^ k3) == expected;
        }
    }

    /// <summary>
    /// String storage core behind <see cref="SecureObservableString"/>: the payload is a
    /// per-character masked buffer instead of a single word, so the seal/unseal pair is
    /// type-specific while the rest of the protocol is shared.
    /// </summary>
    internal sealed class SecureStringCore : SecureObservableCore<string>
    {
        private const int GuardConst = unchecked((int)0x4E455855);
        private const int ShadowGuardConst = unchecked((int)0x53484457);

        private char[] _obscuredChars; // null ⟺ value is null
        private int _cryptoKey1;
        private int _cryptoKey2;
        private int _guard;
        private char[] _shadowChars;
        private int _shadowKey;
        private int _shadowGuard;

        public SecureStringCore(string initialValue, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(nameof(SecureObservableString), debugName, staticTamperAccessor)
        {
            Initialize(initialValue);
        }

        protected override string DefaultValue => null;
        protected override bool ValuesEqual(string left, string right) => string.Equals(left, right);

        protected override void SealValue(string value)
        {
            var (k1, k2) = SecureKeyGen.IntKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            int compound = k1 ^ k2;
            _obscuredChars = Obscure(value, compound);
            _guard = compound ^ GuardConst;

            int k3;
            do { k3 = SecureKeyGen.NextIntKey(); } while (k3 == compound);
            _shadowKey = k3;
            _shadowChars = Obscure(value, k3);
            _shadowGuard = k3 ^ ShadowGuardConst;
        }

        protected override bool TryUnsealPrimary(out string value)
        {
            int compound = _cryptoKey1 ^ _cryptoKey2;
            if ((compound ^ GuardConst) != _guard)
            {
                value = null;
                return false;
            }
            value = Reveal(_obscuredChars, compound);
            return true;
        }

        protected override bool TryUnsealRedundant(out string value)
        {
            int k3 = _shadowKey;
            if ((k3 ^ ShadowGuardConst) != _shadowGuard)
            {
                value = null;
                return false;
            }
            value = Reveal(_shadowChars, k3);
            return true;
        }

        protected override bool VerifySeal(string expected)
        {
            int compound = _cryptoKey1 ^ _cryptoKey2;
            if ((_guard ^ GuardConst) != compound) return false;
            if (!string.Equals(Reveal(_obscuredChars, compound), expected)) return false;

            int k3 = _shadowKey;
            if ((_shadowGuard ^ ShadowGuardConst) != k3) return false;
            return string.Equals(Reveal(_shadowChars, k3), expected);
        }

        // XOR each UTF-16 code unit with the low 16 bits of a position-mixed key.
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
    }

    /// <summary>
    /// BigDouble storage core behind <see cref="SecureObservableBigDouble"/>: the mantissa bit
    /// pattern and the exponent are TWO 64-bit words, so the seal/unseal pair is type-specific
    /// while the rest of the protocol is shared. Both words live under the same key set and the
    /// same lock, so the composite pair can never be read or written half-updated (the reason
    /// the old implementation needed a separate composite lock over two inner observables).
    /// </summary>
    internal sealed class SecureBigDoubleCore : SecureObservableCore<BigDouble>
    {
        private const long GuardConst = 0x4E4558554E455855L;        // "NEXUNEXU"
        private const long ShadowGuardConst = 0x5348445753484457L;  // "SHDWSHDW"

        private long _obscuredMantissa;
        private long _obscuredExponent;
        private long _cryptoKey1;
        private long _cryptoKey2;
        private long _guard;
        private long _shadowMantissa;
        private long _shadowExponent;
        private long _shadowKey;
        private long _shadowGuard;

        public SecureBigDoubleCore(BigDouble initialValue, string debugName, Func<Action<string>> staticTamperAccessor)
            : base(nameof(SecureObservableBigDouble), debugName, staticTamperAccessor)
        {
            Initialize(initialValue);
        }

        protected override BigDouble DefaultValue => BigDouble.Zero;
        protected override bool ValuesEqual(BigDouble left, BigDouble right) => left.Equals(right);

        protected override void SealValue(BigDouble value)
        {
            long mantissaBits = BitConverter.DoubleToInt64Bits(value.Mantissa);
            long exponent = value.Exponent;

            var (k1, k2) = SecureKeyGen.LongKeyPair();
            _cryptoKey1 = k1;
            _cryptoKey2 = k2;
            long compound = k1 ^ k2;
            _obscuredMantissa = mantissaBits ^ compound;
            _obscuredExponent = exponent ^ compound;
            _guard = compound ^ GuardConst;

            long k3;
            do { k3 = SecureKeyGen.NextLongKey(); } while (k3 == compound);
            _shadowKey = k3;
            _shadowMantissa = mantissaBits ^ k3;
            _shadowExponent = exponent ^ k3;
            _shadowGuard = k3 ^ ShadowGuardConst;
        }

        protected override bool TryUnsealPrimary(out BigDouble value)
        {
            long compound = _cryptoKey1 ^ _cryptoKey2;
            if ((compound ^ GuardConst) != _guard)
            {
                value = default;
                return false;
            }
            value = Compose(_obscuredMantissa ^ compound, _obscuredExponent ^ compound);
            return true;
        }

        protected override bool TryUnsealRedundant(out BigDouble value)
        {
            long k3 = _shadowKey;
            if ((k3 ^ ShadowGuardConst) != _shadowGuard)
            {
                value = default;
                return false;
            }
            value = Compose(_shadowMantissa ^ k3, _shadowExponent ^ k3);
            return true;
        }

        protected override bool VerifySeal(BigDouble expected)
        {
            long compound = _cryptoKey1 ^ _cryptoKey2;
            if ((_guard ^ GuardConst) != compound) return false;
            if (!ValuesEqual(Compose(_obscuredMantissa ^ compound, _obscuredExponent ^ compound), expected)) return false;

            long k3 = _shadowKey;
            if ((_shadowGuard ^ ShadowGuardConst) != k3) return false;
            return ValuesEqual(Compose(_shadowMantissa ^ k3, _shadowExponent ^ k3), expected);
        }

        private static BigDouble Compose(long mantissaBits, long exponent)
            => new BigDouble(BitConverter.Int64BitsToDouble(mantissaBits), exponent);
    }
}
