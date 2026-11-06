using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Currency balances. Amounts (#196): <see cref="Spend(string,long,string)"/>, <see cref="Earn"/>,
    /// <see cref="SpendBig(string,BigDouble,string)"/> and <see cref="EarnBig"/> take a positive amount; <see cref="CanAfford"/>,
    /// <see cref="CanAffordBig"/>, <see cref="SetBalance"/> and <see cref="SetBigBalance"/> a nonnegative one. Any other amount
    /// (zero where a change is required, negative, NaN, infinite) is a programming error and throws
    /// <see cref="ArgumentOutOfRangeException"/>. A spend the service refuses at run time returns false, and the overload with an
    /// <see cref="EconomyRefusal"/> says why. When the network validator refuses a spend the service has already applied, the
    /// amount comes back and <see cref="SpendRefundedSignal"/> is fired.
    /// </summary>
    public interface IEconomyService
    {
        long GetBalance(string currencyId);
        /// <summary>
        /// The currency's observable balance, read-only: the balance changes through the service, which checks and saves each
        /// change (#231). Its change handlers run synchronously while the service holds its balance
        /// lock (the long view of a BigDouble balance depends on that), so a handler must not wait for another thread
        /// that uses the economy; update UI or post the work instead. The same holds for <see cref="GetObservableBigBalance"/>.
        /// </summary>
        IReadOnlyObservableProperty<long> GetObservableBalance(string currencyId);
        bool CanAfford(string currencyId, long amount);
        bool Spend(string currencyId, long amount, string reason = "");
        /// <summary>As <see cref="Spend(string,long,string)"/>; on false, <paramref name="refusal"/> says why.</summary>
        bool Spend(string currencyId, long amount, out EconomyRefusal refusal, string reason = "");
        void Earn(string currencyId, long amount, string reason = "");
        void SetBalance(string currencyId, long amount);

        // BigDouble API for idle, incremental, and large-scale numeric game genres
        BigDouble GetBigBalance(string currencyId);
        IReadOnlyObservableProperty<BigDouble> GetObservableBigBalance(string currencyId);
        bool CanAffordBig(string currencyId, BigDouble amount);
        /// <summary>Throws <see cref="NotSupportedException"/> when the bound network validator is not an <see cref="INetworkBigEconomyValidator"/>, as <see cref="EarnBig"/> and <see cref="SetBigBalance"/> do.</summary>
        bool SpendBig(string currencyId, BigDouble amount, string reason = "");
        /// <summary>As <see cref="SpendBig(string,BigDouble,string)"/>; on false, <paramref name="refusal"/> says why.</summary>
        bool SpendBig(string currencyId, BigDouble amount, out EconomyRefusal refusal, string reason = "");
        void EarnBig(string currencyId, BigDouble amount, string reason = "");
        void SetBigBalance(string currencyId, BigDouble amount);
    }

    /// <summary>Why a spend returned false (#196).</summary>
    public enum EconomyRefusal
    {
        /// <summary>The spend was applied.</summary>
        None,
        /// <summary>The balance is below the amount.</summary>
        InsufficientBalance,
        /// <summary>The currency id is null or empty.</summary>
        InvalidCurrencyId,
        /// <summary>The service is disposed or being disposed.</summary>
        Disposed,
    }

    /// <summary>
    /// The network validator refused a spend the service had already applied, and the amount is back in the balance: a game
    /// that granted something for the spend takes it back here (#196). Fired with <c>FireThreadSafe</c> from the thread the
    /// validator completes on, so subscribers receive it on the main thread at the next drain.
    /// </summary>
    public readonly struct SpendRefundedSignal
    {
        public readonly string CurrencyId;
        /// <summary>The refunded amount; a BigDouble refund above <see cref="long.MaxValue"/> saturates here.</summary>
        public readonly long Amount;
        /// <summary>The refunded amount, exact.</summary>
        public readonly BigDouble BigAmount;
        /// <summary>The reason the spend was made with.</summary>
        public readonly string Reason;

        public SpendRefundedSignal(string currencyId, long amount, BigDouble bigAmount, string reason)
        {
            CurrencyId = currencyId;
            Amount = amount;
            BigAmount = bigAmount;
            Reason = reason;
        }
    }

    public interface INetworkEconomyValidator
    {
        Task<bool> ValidateSpendAsync(string currencyId, long amount, string reason);
        Task ValidateEarnAsync(string currencyId, long amount, string reason);
    }

    /// <summary>Optional backend capability for balances beyond the long range.</summary>
    public interface INetworkBigEconomyValidator : INetworkEconomyValidator
    {
        Task<bool> ValidateSpendBigAsync(string currencyId, BigDouble amount, string reason);
        Task ValidateEarnBigAsync(string currencyId, BigDouble amount, string reason);
    }

    [Preserve]
    public class EconomyService : NexusService<IEconomyService>, IEconomyService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: the game's store for balances (SettingsStores.Economy); null uses PlayerPrefsService.
        [OptionalInject] public SettingsStores SettingsStores { get; set; }
        private IPlayerPrefsService Store => SettingsStores?.Economy ?? PlayerPrefsService;
        // Optional capability: every use is null-checked and fire-and-forget, so a game
        // without a backend must not fail strict injection / startup validation over it.
        [OptionalInject] public INetworkEconomyValidator NetworkValidator { get; set; }
        // Optional write-coalescer: when bound, balance persistence is throttled to one
        // batched flush per window instead of a synchronous PlayerPrefs.Save() per
        // Earn/Spend (frame hitching on mobile). Without a binding, saves are immediate
        // (previous behavior, preserved for tests and bare containers).
        [OptionalInject] public ISaveThrottler SaveThrottler { get; set; }

        // Owner id for the shared SaveThrottler: EconomyService and ProgressionService may
        // share ONE throttler singleton, so each must use its own slot — otherwise one's
        // pending write silently clobbers the other's (the pre-multi-owner data-loss bug).
        private const string SaveOwner = "economy";
        private Action _persistAllBalancesNow;

        // Anti-cheat: balances are XOR-masked in RAM (SecureObservableLong), matching the
        // project's SecureObservableInt story for the most valuable (currency) data. This
        // obscures simple memory scans of the balance dictionary. Modified clients can
        // bypass this; server validation/authority must protect authoritative currency.
        // ConcurrentDictionary so the lock-free TryGetValue fast path in
        // GetObservableBalance is actually safe: a plain Dictionary can tear/corrupt on
        // concurrent read/write even for different keys (rehash), and Dispose() mutates
        // it — the previous comment claiming lock-free reads were safe was wrong.
        private readonly ConcurrentDictionary<string, SecureObservableLong> _balances = new();
        private readonly ConcurrentDictionary<string, SecureObservableBigDouble> _bigBalances = new();
        // Both currency modes share one mutation lock, a dedicated object (CONTRIBUTING: never lock a concurrent
        // collection itself). A promoted currency's long observable remains the same instance, projecting its
        // canonical BigDouble balance.
        private readonly object _balanceLock = new object();
        // Under _balanceLock: the BigDouble currencies whose legacy long key a flush has deleted (#300).
        private readonly HashSet<string> _legacyKeyDeleted = new();
        // A currency's first touch reads storage (file I/O and decryption with EncryptedStorageService). The read
        // runs before the balance lock (PrefetchSaved) and the create path under the lock takes its result.
        private readonly ConcurrentDictionary<string, SavedBalance> _prefetched = new();
        private volatile bool _disposed;
        private volatile bool _disposing;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public long GetBalance(string currencyId)
        {
            return GetObservableBalance(currencyId)?.Value ?? 0L;
        }

        public bool CanAfford(string currencyId, long amount)
        {
            RequireNonnegative(amount);
            if (amount == 0) return true;
            return GetBalance(currencyId) >= amount;
        }

        public bool Spend(string currencyId, long amount, string reason = "") => Spend(currencyId, amount, out _, reason);

        public bool Spend(string currencyId, long amount, out EconomyRefusal refusal, string reason = "")
        {
            RequirePositive(amount);
            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                refusal = RefusalLocked(currencyId);
                if (refusal != EconomyRefusal.None) return false;
                var prop = LazyLoadBalance(currencyId);
                if (_bigBalances.TryGetValue(currencyId, out var big))
                {
                    var cost = new BigDouble(amount);
                    if (big.Value < cost)
                    {
                        refusal = EconomyRefusal.InsufficientBalance;
                        return false;
                    }
                    big.SetByService(big.Value - cost);
                }
                else
                {
                    if (prop.Value < amount)
                    {
                        refusal = EconomyRefusal.InsufficientBalance;
                        return false;
                    }
                    prop.SetByService(prop.Value - amount);
                }
            }

            // I/O and network calls outside the lock so slow storage or a
            // fire-and-forget network validation never stalls other balance operations.
            SchedulePersist();

            if (NetworkValidator != null)
            {
                _ = ReconcileSpendAsync(currencyId, amount, reason);
            }
            return true;
        }

        public void Earn(string currencyId, long amount, string reason = "")
        {
            RequirePositive(amount);
            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                AddBalanceLocked(currencyId, amount);
            }

            SchedulePersist();

            if (NetworkValidator != null)
            {
                _ = SafeValidateEarnAsync(currencyId, amount, reason);
            }
        }

        public void SetBalance(string currencyId, long amount)
        {
            RequireNonnegative(amount);
            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                var prop = LazyLoadBalance(currencyId);
                if (_bigBalances.TryGetValue(currencyId, out var big)) big.SetByService(new BigDouble(amount));
                else prop.SetByService(amount);
            }
            SchedulePersist();
        }

        // Under the lock: the run-time reason a spend cannot start.
        private EconomyRefusal RefusalLocked(string currencyId)
            => _disposed || _disposing ? EconomyRefusal.Disposed
                : string.IsNullOrEmpty(currencyId) ? EconomyRefusal.InvalidCurrencyId
                : EconomyRefusal.None;

        // #196: an amount outside the method's range is a programming error, never a silent no-op.
        private static void RequirePositive(long amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must be positive.");
        }

        private static void RequireNonnegative(long amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must not be negative.");
        }

        private static void RequirePositive(BigDouble amount)
        {
            if (!IsFiniteNonnegative(amount) || amount.Mantissa == 0)
                throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be finite and positive.");
        }

        private static void RequireNonnegative(BigDouble amount)
        {
            if (!IsFiniteNonnegative(amount))
                throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be finite and not negative.");
        }

        // Reads are lock-free (ConcurrentDictionary.TryGetValue); the
        // explicit lock is taken ONLY on the create path and in Spend/Earn/SetBalance —
        // where the check-then-mutate sequence on SecureObservableLong.Value must be
        // atomic against other mutators (two concurrent Spends could both pass the
        // CanAfford check). Do NOT remove the lock from mutation paths.
        public IReadOnlyObservableProperty<long> GetObservableBalance(string currencyId)
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return null;
            if (_balances.TryGetValue(currencyId, out var existing))
                return existing;

            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing) return null;
                return LazyLoadBalance(currencyId);
            }
        }

        private readonly struct SavedBalance
        {
            public readonly bool HasBig;
            public readonly BigDouble Big;
            public readonly bool HasLong;
            public readonly long Long;
            public SavedBalance(bool hasBig, BigDouble big, bool hasLong, long value) { HasBig = hasBig; Big = big; HasLong = hasLong; Long = value; }
        }

        // Outside the lock, before every path that may create a currency. A loaded currency costs one
        // lock-free lookup and no allocation (GCA5); the long balance decides because a big balance
        // created next to a loaded long one starts from that long value, not from storage.
        private void PrefetchSaved(string currencyId)
        {
            if (string.IsNullOrEmpty(currencyId) || _balances.ContainsKey(currencyId) || _prefetched.ContainsKey(currencyId)) return;
            _prefetched[currencyId] = ReadSaved(currencyId);
        }

        private SavedBalance ReadSaved(string currencyId)
        {
            var store = Store;
            if (store == null) return default;
            if (store.HasKey($"NT_EcoBig_{currencyId}"))
                return new SavedBalance(true, store.GetBigDouble($"NT_EcoBig_{currencyId}", BigDouble.Zero), false, 0L);
            bool hasLong = store.HasKey($"NT_Eco_{currencyId}");
            return new SavedBalance(false, BigDouble.Zero, hasLong, hasLong ? store.GetLong($"NT_Eco_{currencyId}", 0L) : 0L);
        }

        // Under the lock: the prefetched read, or (a creating path that raced the prefetch) a direct read.
        private SavedBalance TakeSaved(string currencyId)
            => _prefetched.TryRemove(currencyId, out var saved) ? saved : ReadSaved(currencyId);

        // Must be called under _balanceLock.
        private SecureObservableLong LazyLoadBalance(string currencyId)
            => _balances.TryGetValue(currencyId, out var prop) ? prop : CreateBalanceLocked(currencyId);

        private SecureObservableLong CreateBalanceLocked(string currencyId)
        {
            var saved = TakeSaved(currencyId);
            // A persisted promotion remains canonical after restart, including for old
            // callers which only use the long API.
            if (!_bigBalances.ContainsKey(currencyId) && saved.HasBig)
                CreateBigBalanceLocked(currencyId, saved);
            long savedAmount = _bigBalances.TryGetValue(currencyId, out var big)
                ? ProjectLong(big.Value)
                : saved.Long;
            var prop = new SecureObservableLong(Math.Max(0, savedAmount));
            _balances[currencyId] = prop;
            return prop;
        }

        // ─── BigDouble Idle / Incremental API Implementation ───

        public BigDouble GetBigBalance(string currencyId)
        {
            var prop = GetObservableBigBalance(currencyId);
            return prop?.Value ?? BigDouble.Zero;
        }

        public bool CanAffordBig(string currencyId, BigDouble amount)
        {
            RequireNonnegative(amount);
            if (amount.Mantissa == 0) return true;
            // the amount SpendBig would take: a price built field by field is normalized and capped first (issue #232)
            return GetBigBalance(currencyId) >= Normalize(amount);
        }

        public bool SpendBig(string currencyId, BigDouble amount, string reason = "") => SpendBig(currencyId, amount, out _, reason);

        public bool SpendBig(string currencyId, BigDouble amount, out EconomyRefusal refusal, string reason = "")
        {
            RequirePositive(amount);
            var validator = RequireBigValidator();
            amount = Normalize(amount);

            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                refusal = RefusalLocked(currencyId);
                if (refusal != EconomyRefusal.None) return false;
                var prop = LazyLoadBigBalance(currencyId);
                if (prop.Value < amount)
                {
                    refusal = EconomyRefusal.InsufficientBalance;
                    return false;
                }
                prop.SetByService(prop.Value - amount);
            }

            SchedulePersist();
            if (validator != null) _ = ReconcileBigSpendAsync(validator, currencyId, amount, reason);
            return true;
        }

        public void EarnBig(string currencyId, BigDouble amount, string reason = "")
        {
            RequirePositive(amount);
            var validator = RequireBigValidator();
            amount = Normalize(amount);

            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                var prop = LazyLoadBigBalance(currencyId);
                prop.SetByService(Normalize(prop.Value + amount));
            }

            SchedulePersist();
            if (validator != null) _ = SafeValidateBigEarnAsync(validator, currencyId, amount, reason);
        }

        public void SetBigBalance(string currencyId, BigDouble amount)
        {
            RequireNonnegative(amount);
            RequireBigValidator();
            amount = Normalize(amount);
            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                LazyLoadBigBalance(currencyId).SetByService(amount);
            }
            SchedulePersist();
        }

        public IReadOnlyObservableProperty<BigDouble> GetObservableBigBalance(string currencyId)
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return null;
            if (_bigBalances.TryGetValue(currencyId, out var existing))
                return existing;

            PrefetchSaved(currencyId);
            lock (_balanceLock)
            {
                if (_disposed || _disposing) return null;
                return LazyLoadBigBalance(currencyId);
            }
        }

        // Must be called under _balanceLock. Promotion is one-way per currency.
        private SecureObservableBigDouble LazyLoadBigBalance(string currencyId)
            => _bigBalances.TryGetValue(currencyId, out var prop) ? prop
                : CreateBigBalanceLocked(currencyId, _balances.ContainsKey(currencyId) ? default : TakeSaved(currencyId));

        // Split from the lookup: the OnChanged lambda captures currencyId, and C# allocates a captured
        // parameter's closure on method entry, so in one method every big Earn/Spend paid for it.
        private SecureObservableBigDouble CreateBigBalanceLocked(string currencyId, SavedBalance saved)
        {
            BigDouble savedAmount = BigDouble.Zero;
            if (_balances.TryGetValue(currencyId, out var existingLong))
            {
                savedAmount = new BigDouble(existingLong.Value);
            }
            else if (saved.HasBig)
            {
                savedAmount = saved.Big;
            }
            else if (saved.HasLong)
            {
                savedAmount = new BigDouble(saved.Long);
            }
            savedAmount = IsFiniteNonnegative(savedAmount) ? Normalize(savedAmount) : BigDouble.Zero;
            var prop = new SecureObservableBigDouble(savedAmount, $"EcoBig_{currencyId}");
            _bigBalances[currencyId] = prop;
            prop.OnChanged((_, value) =>
            {
                lock (_balanceLock)
                {
                    if (_disposed || _disposing) return;
                    ProjectLegacyLocked(currencyId, value);
                }
            });
            ProjectLegacyLocked(currencyId, savedAmount);
            return prop;
        }

        private static bool IsFiniteNonnegative(BigDouble value)
            => !double.IsNaN(value.Mantissa) && !double.IsInfinity(value.Mantissa) && value.Mantissa >= 0;

        private static BigDouble Normalize(BigDouble value)
        {
            var normalized = new BigDouble(value.Mantissa, value.Exponent);
            return normalized > BigDouble.MaxValue ? BigDouble.MaxValue : normalized;
        }

        private INetworkBigEconomyValidator RequireBigValidator()
        {
            if (NetworkValidator == null) return null;
            if (NetworkValidator is INetworkBigEconomyValidator validator) return validator;
            throw new NotSupportedException("The configured economy backend does not validate BigDouble transactions.");
        }

        private static long ProjectLong(BigDouble value)
        {
            if (!IsFiniteNonnegative(value) || value.Mantissa == 0) return 0;
            if (value >= new BigDouble(long.MaxValue)) return long.MaxValue;
            double number = (double)value;
            return number >= 9223372036854775808d ? long.MaxValue : (long)number;
        }

        private void ProjectLegacyLocked(string currencyId, BigDouble value)
        {
            if (_balances.TryGetValue(currencyId, out var legacy)) legacy.SetByService(ProjectLong(value));
        }

        private void AddBalanceLocked(string currencyId, long amount)
        {
            var prop = LazyLoadBalance(currencyId);
            if (_bigBalances.TryGetValue(currencyId, out var big)) big.SetByService(Normalize(big.Value + new BigDouble(amount)));
            else
            {
                prop.SetByService(amount > long.MaxValue - prop.Value ? long.MaxValue : prop.Value + amount);
            }
        }
        /// <summary>
        /// Schedules a balance persist. With a <see cref="SaveThrottler"/> bound, per-mutation
        /// writes coalesce into one batched flush every throttle window (the action always
        /// saves ALL currencies, so a single batched flush can never drop a currency).
        /// Without one, persists immediately (previous behavior).
        /// </summary>
        private void SchedulePersist()
        {
            if (_disposed || _disposing) return;
            if (SaveThrottler != null)
            {
                // Cached: a method group converts to a new delegate on every call.
                SaveThrottler.TryRequestSave(SaveOwner, _persistAllBalancesNow ??= PersistAllBalancesNow);
            }
            else
            {
                PersistAllBalancesNow();
            }
        }

        private void PersistAllBalancesNow()
        {
            var store = Store;
            if (store == null) return;
            lock (_balanceLock)
            {
                if (_disposed) return;
                foreach (var kvp in _balances)
                {
                    if (!_bigBalances.ContainsKey(kvp.Key))
                        store.SetLong($"NT_Eco_{kvp.Key}", kvp.Value.Value);
                }
                foreach (var kvp in _bigBalances)
                {
                    store.SetBigDouble($"NT_EcoBig_{kvp.Key}", kvp.Value.Value);
                    // The legacy long key goes once, after the first write of the big one (#300): each flush deleted it
                    // again, with the store's disk checks under this lock.
                    if (_legacyKeyDeleted.Add(kvp.Key)) store.DeleteKey($"NT_Eco_{kvp.Key}");
                }
            }
            // Flush outside the lock (may hit disk) — mirrors ProgressionService.PersistNow.
            store.Save();
        }

        /// <summary>
        /// Awaits the network spend validation and, if the server REJECTS the spend,
        /// restores the optimistically-deducted amount (bounded at <see cref="long.MaxValue"/>).
        /// Fire-and-forget on purpose: the local commit must never block the caller.
        /// </summary>
        private async Task ReconcileSpendAsync(string currencyId, long amount, string reason)
        {
            try
            {
                bool approved = await NetworkValidator.ValidateSpendAsync(currencyId, amount, reason);
                if (approved) return;
                PrefetchSaved(currencyId);
                lock (_balanceLock)
                {
                    if (_disposed || _disposing) return;
                    AddBalanceLocked(currencyId, amount);
                }
                // No stale callback may schedule work after teardown. Persist actions also
                // check the lifetime under the balance lock when a throttler invokes them.
                if (_disposed || _disposing) return;
                if (SaveThrottler != null) SaveThrottler.ForceSave(SaveOwner, PersistAllBalancesNow);
                else PersistAllBalancesNow();
                NexusLog.WarnAlways($"[Economy] Server rejected spend of {amount} '{currencyId}' — balance restored.");
                SignalBus?.FireThreadSafe(new SpendRefundedSignal(currencyId, amount, new BigDouble(amount), reason));
            }
            catch (Exception ex)
            {
                // Network failure: keep the optimistic balance; a later reconciliation (or the
                // server's authoritative ledger) settles the difference. Surface for debugging.
                NexusLog.WarnAlways($"[Economy] Spend validation failed for '{currencyId}': {ex.Message}");
            }
        }

        /// <summary>Fire-and-forget earn validation wrapper so unobserved task exceptions never escape.</summary>
        private async Task SafeValidateEarnAsync(string currencyId, long amount, string reason)
        {
            try
            {
                await NetworkValidator.ValidateEarnAsync(currencyId, amount, reason);
            }
            catch (Exception ex)
            {
                NexusLog.WarnAlways($"[Economy] Earn validation failed for '{currencyId}': {ex.Message}");
            }
        }

        private async Task ReconcileBigSpendAsync(INetworkBigEconomyValidator validator, string currencyId, BigDouble amount, string reason)
        {
            try
            {
                if (await validator.ValidateSpendBigAsync(currencyId, amount, reason)) return;
                PrefetchSaved(currencyId);
                lock (_balanceLock)
                {
                    if (_disposed || _disposing) return;
                    var prop = LazyLoadBigBalance(currencyId);
                    prop.SetByService(Normalize(prop.Value + amount));
                }
                if (_disposed || _disposing) return;
                if (SaveThrottler != null) SaveThrottler.ForceSave(SaveOwner, PersistAllBalancesNow);
                else PersistAllBalancesNow();
                NexusLog.WarnAlways($"[Economy] Server rejected spend of {amount} '{currencyId}' — balance restored.");
                SignalBus?.FireThreadSafe(new SpendRefundedSignal(currencyId, ProjectLong(amount), amount, reason));
            }
            catch (Exception ex) { NexusLog.WarnAlways($"[Economy] Big spend validation failed for '{currencyId}': {ex.Message}"); }
        }

        private async Task SafeValidateBigEarnAsync(INetworkBigEconomyValidator validator, string currencyId, BigDouble amount, string reason)
        {
            try { await validator.ValidateEarnBigAsync(currencyId, amount, reason); }
            catch (Exception ex) { NexusLog.WarnAlways($"[Economy] Big earn validation failed for '{currencyId}': {ex.Message}"); }
        }

        public override void Dispose()
        {
            lock (_balanceLock)
            {
                if (_disposed || _disposing) return;
                _disposing = true;
            }
            try
            {
                // ForceSave may queue behind an in-flight save. Commit the final state
                // directly before clearing it; queued actions become harmless after disposal.
                PersistAllBalancesNow();
            }
            catch (Exception ex) { NexusLog.WarnAlways($"[Economy] Final persist failed on dispose: {ex.Message}"); }
            finally
            {
                lock (_balanceLock)
                {
                    _disposed = true;
                    foreach (var kvp in _balances) kvp.Value.ClearOnChanged();
                    _balances.Clear();
                    foreach (var kvp in _bigBalances) kvp.Value.ClearOnChanged();
                    _bigBalances.Clear();
                    _prefetched.Clear();
                }
            }
        }
    }
}
