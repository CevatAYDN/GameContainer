using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface IEconomyService
    {
        long GetBalance(string currencyId);
        SecureObservableLong GetObservableBalance(string currencyId);
        bool CanAfford(string currencyId, long amount);
        bool Spend(string currencyId, long amount, string reason = "");
        void Earn(string currencyId, long amount, string reason = "");
        void SetBalance(string currencyId, long amount);

        // BigDouble API for idle, incremental, and large-scale numeric game genres
        BigDouble GetBigBalance(string currencyId);
        SecureObservableBigDouble GetObservableBigBalance(string currencyId);
        bool CanAffordBig(string currencyId, BigDouble amount);
        bool SpendBig(string currencyId, BigDouble amount, string reason = "");
        void EarnBig(string currencyId, BigDouble amount, string reason = "");
        void SetBigBalance(string currencyId, BigDouble amount);
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
        // Both currency modes share _balances' mutation lock. A promoted currency's long
        // observable remains the same instance, projecting its canonical BigDouble balance.
        private readonly Dictionary<string, long> _projectingLegacy = new();
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
            if (amount <= 0) return true;
            return GetBalance(currencyId) >= amount;
        }

        public bool Spend(string currencyId, long amount, string reason = "")
        {
            if (amount <= 0) return true;

            SecureObservableLong prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return false;
                prop = LazyLoadBalance(currencyId);
                if (_bigBalances.TryGetValue(currencyId, out var big))
                {
                    if (big.Value < new BigDouble(amount)) return false;
                    big.Value -= new BigDouble(amount);
                }
                else
                {
                    if (prop.Value < amount) return false;
                    prop.Value -= amount;
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
            if (amount <= 0) return;

            SecureObservableLong prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                prop = LazyLoadBalance(currencyId);
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
            SecureObservableLong prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                prop = LazyLoadBalance(currencyId);
                if (_bigBalances.TryGetValue(currencyId, out var big)) big.Value = new BigDouble(Math.Max(0L, amount));
                else prop.Value = Math.Max(0L, amount);
            }
            SchedulePersist();
        }

        // Reads are lock-free (ConcurrentDictionary.TryGetValue); the
        // explicit lock is taken ONLY on the create path and in Spend/Earn/SetBalance —
        // where the check-then-mutate sequence on SecureObservableLong.Value must be
        // atomic against other mutators (two concurrent Spends could both pass the
        // CanAfford check). Do NOT remove the lock from mutation paths.
        public SecureObservableLong GetObservableBalance(string currencyId)
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return null;
            if (_balances.TryGetValue(currencyId, out var existing))
                return existing;

            lock (_balances)
            {
                if (_disposed || _disposing) return null;
                return LazyLoadBalance(currencyId);
            }
        }

        // Must be called under _balances lock.
        private SecureObservableLong LazyLoadBalance(string currencyId)
        {
            if (_balances.TryGetValue(currencyId, out var prop))
                return prop;

            // A persisted promotion remains canonical after restart, including for old
            // callers which only use the long API.
            if (!_bigBalances.ContainsKey(currencyId) && PlayerPrefsService?.HasKey($"NT_EcoBig_{currencyId}") == true)
                LazyLoadBigBalance(currencyId);
            long savedAmount = _bigBalances.TryGetValue(currencyId, out var big)
                ? ProjectLong(big.Value)
                : PlayerPrefsService?.GetLong($"NT_Eco_{currencyId}", 0L) ?? 0L;
            prop = new SecureObservableLong(Math.Max(0, savedAmount));
            _balances[currencyId] = prop;
            prop.OnChanged((_, value) =>
            {
                lock (_balances)
                {
                    if (_disposed || _disposing || !_bigBalances.TryGetValue(currencyId, out var canonical)) return;
                    if (_projectingLegacy.TryGetValue(currencyId, out long projection) && value == projection) return;
                    // Preserve existing mutable observable ownership after promotion.
                    canonical.Value = new BigDouble(Math.Max(0L, value));
                }
            });
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
            if (!IsFiniteNonnegative(amount)) return false;
            if (amount.Mantissa == 0) return true;
            return GetBigBalance(currencyId) >= amount;
        }

        public bool SpendBig(string currencyId, BigDouble amount, string reason = "")
        {
            if (!IsFiniteNonnegative(amount)) return false;
            if (amount.Mantissa == 0) return true;
            var validator = NetworkValidator;
            if (validator != null && !(validator is INetworkBigEconomyValidator)) return false;
            amount = Normalize(amount);

            SecureObservableBigDouble prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return false;
                prop = LazyLoadBigBalance(currencyId);
                if (prop.Value < amount) return false;
                prop.Value -= amount;
            }

            SchedulePersist();
            if (validator is INetworkBigEconomyValidator bigValidator)
                _ = ReconcileBigSpendAsync(bigValidator, currencyId, amount, reason);
            return true;
        }

        public void EarnBig(string currencyId, BigDouble amount, string reason = "")
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
            ValidateBigMutation(amount);
            if (amount.Mantissa == 0) return;
            var validator = RequireBigValidator();
            amount = Normalize(amount);

            SecureObservableBigDouble prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                prop = LazyLoadBigBalance(currencyId);
                prop.Value = Normalize(prop.Value + amount);
            }

            SchedulePersist();
            if (validator != null) _ = SafeValidateBigEarnAsync(validator, currencyId, amount, reason);
        }

        public void SetBigBalance(string currencyId, BigDouble amount)
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
            ValidateBigMutation(amount);
            RequireBigValidator();
            amount = Normalize(amount);
            SecureObservableBigDouble prop;
            lock (_balances)
            {
                if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return;
                prop = LazyLoadBigBalance(currencyId);
                prop.Value = amount;
            }
            SchedulePersist();
        }

        public SecureObservableBigDouble GetObservableBigBalance(string currencyId)
        {
            if (_disposed || _disposing || string.IsNullOrEmpty(currencyId)) return null;
            if (_bigBalances.TryGetValue(currencyId, out var existing))
                return existing;

            lock (_balances)
            {
                if (_disposed || _disposing) return null;
                return LazyLoadBigBalance(currencyId);
            }
        }

        // Must be called under _balances lock. Promotion is one-way per currency.
        private SecureObservableBigDouble LazyLoadBigBalance(string currencyId)
        {
            if (_bigBalances.TryGetValue(currencyId, out var prop))
                return prop;

            BigDouble savedAmount = BigDouble.Zero;
            if (_balances.TryGetValue(currencyId, out var existingLong))
            {
                savedAmount = new BigDouble(existingLong.Value);
            }
            else if (PlayerPrefsService != null)
            {
                if (PlayerPrefsService.HasKey($"NT_EcoBig_{currencyId}"))
                {
                    savedAmount = PlayerPrefsService.GetBigDouble($"NT_EcoBig_{currencyId}", BigDouble.Zero);
                }
                else if (PlayerPrefsService.HasKey($"NT_Eco_{currencyId}"))
                {
                    savedAmount = new BigDouble(PlayerPrefsService.GetLong($"NT_Eco_{currencyId}", 0L));
                }
            }
            savedAmount = IsFiniteNonnegative(savedAmount) ? Normalize(savedAmount) : BigDouble.Zero;
            prop = new SecureObservableBigDouble(savedAmount, $"EcoBig_{currencyId}");
            _bigBalances[currencyId] = prop;
            prop.OnChanged((_, value) =>
            {
                lock (_balances)
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

        private static void ValidateBigMutation(BigDouble amount)
        {
            if (!IsFiniteNonnegative(amount)) throw new ArgumentOutOfRangeException(nameof(amount), "Currency amounts must be finite and nonnegative.");
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
            if (!_balances.TryGetValue(currencyId, out var legacy)) return;
            long projection = ProjectLong(value);
            bool hadPrevious = _projectingLegacy.TryGetValue(currencyId, out long previous);
            _projectingLegacy[currencyId] = projection;
            try { legacy.Value = projection; }
            finally
            {
                if (hadPrevious) _projectingLegacy[currencyId] = previous;
                else _projectingLegacy.Remove(currencyId);
            }
        }

        private void AddBalanceLocked(string currencyId, long amount)
        {
            var prop = LazyLoadBalance(currencyId);
            if (_bigBalances.TryGetValue(currencyId, out var big)) big.Value = Normalize(big.Value + new BigDouble(amount));
            else
            {
                prop.Value = amount > long.MaxValue - prop.Value ? long.MaxValue : prop.Value + amount;
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
                SaveThrottler.TryRequestSave(SaveOwner, PersistAllBalancesNow);
            }
            else
            {
                PersistAllBalancesNow();
            }
        }

        private void PersistAllBalancesNow()
        {
            if (PlayerPrefsService == null) return;
            lock (_balances)
            {
                if (_disposed) return;
                foreach (var kvp in _balances)
                {
                    if (!_bigBalances.ContainsKey(kvp.Key))
                        PlayerPrefsService.SetLong($"NT_Eco_{kvp.Key}", kvp.Value.Value);
                }
                foreach (var kvp in _bigBalances)
                {
                    PlayerPrefsService.SetBigDouble($"NT_EcoBig_{kvp.Key}", kvp.Value.Value);
                    PlayerPrefsService.DeleteKey($"NT_Eco_{kvp.Key}");
                }
            }
            // Flush outside the lock (may hit disk) — mirrors ProgressionService.PersistNow.
            PlayerPrefsService.Save();
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
                lock (_balances)
                {
                    if (_disposed || _disposing) return;
                    AddBalanceLocked(currencyId, amount);
                }
                // No stale callback may schedule work after teardown. Persist actions also
                // check the lifetime under the balance lock when a throttler invokes them.
                if (_disposed || _disposing) return;
                if (SaveThrottler != null) SaveThrottler.ForceSave(SaveOwner, PersistAllBalancesNow);
                else PersistAllBalancesNow();
                NexusRuntime.Logger?.LogWarning($"[Economy] Server rejected spend of {amount} '{currencyId}' — balance restored.");
            }
            catch (Exception ex)
            {
                // Network failure: keep the optimistic balance; a later reconciliation (or the
                // server's authoritative ledger) settles the difference. Surface for debugging.
                NexusRuntime.Logger?.LogWarning($"[Economy] Spend validation failed for '{currencyId}': {ex.Message}");
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
                NexusRuntime.Logger?.LogWarning($"[Economy] Earn validation failed for '{currencyId}': {ex.Message}");
            }
        }

        private async Task ReconcileBigSpendAsync(INetworkBigEconomyValidator validator, string currencyId, BigDouble amount, string reason)
        {
            try
            {
                if (await validator.ValidateSpendBigAsync(currencyId, amount, reason)) return;
                lock (_balances)
                {
                    if (_disposed || _disposing) return;
                    var prop = LazyLoadBigBalance(currencyId);
                    prop.Value = Normalize(prop.Value + amount);
                }
                if (_disposed || _disposing) return;
                if (SaveThrottler != null) SaveThrottler.ForceSave(SaveOwner, PersistAllBalancesNow);
                else PersistAllBalancesNow();
            }
            catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[Economy] Big spend validation failed for '{currencyId}': {ex.Message}"); }
        }

        private async Task SafeValidateBigEarnAsync(INetworkBigEconomyValidator validator, string currencyId, BigDouble amount, string reason)
        {
            try { await validator.ValidateEarnBigAsync(currencyId, amount, reason); }
            catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[Economy] Big earn validation failed for '{currencyId}': {ex.Message}"); }
        }

        public override void Dispose()
        {
            lock (_balances)
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
            catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[Economy] Final persist failed on dispose: {ex.Message}"); }
            finally
            {
                lock (_balances)
                {
                    _disposed = true;
                    foreach (var kvp in _balances) kvp.Value.ClearOnChanged();
                    _balances.Clear();
                    foreach (var kvp in _bigBalances) kvp.Value.ClearOnChanged();
                    _bigBalances.Clear();
                    _projectingLegacy.Clear();
                }
            }
        }
    }
}
