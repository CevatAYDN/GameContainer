using System;
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
    }

    public interface INetworkEconomyValidator
    {
        Task<bool> ValidateSpendAsync(string currencyId, long amount, string reason);
        Task ValidateEarnAsync(string currencyId, long amount, string reason);
    }

    [Preserve]
    public class EconomyService : NexusService<IEconomyService>, IEconomyService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        [Inject] public INetworkEconomyValidator NetworkValidator { get; set; }

        // Anti-cheat: balances are XOR-masked in RAM (SecureObservableLong), matching the
        // project's SecureObservableInt story for the most valuable (currency) data. This
        // defeats GameGuardian / CheatEngine memory scans on the balance dictionary itself.
        private readonly Dictionary<string, SecureObservableLong> _balances = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public long GetBalance(string currencyId)
        {
            var prop = GetObservableBalance(currencyId);
            return prop?.Value ?? 0L;
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
                prop = LazyLoadBalance(currencyId);
                if (prop.Value < amount) return false;
                prop.Value -= amount;
            }

            // I/O and network calls outside the lock so slow storage or a
            // fire-and-forget network validation never stalls other balance operations.
            SaveBalance(currencyId, prop.Value);

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
                prop = LazyLoadBalance(currencyId);
                prop.Value = amount > long.MaxValue - prop.Value ? long.MaxValue : prop.Value + amount;
            }

            SaveBalance(currencyId, prop.Value);

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
                prop = LazyLoadBalance(currencyId);
                prop.Value = Math.Max(0L, amount);
            }
            SaveBalance(currencyId, prop.Value);
        }

        // Lock-free lookup: the balance dictionary is only mutated under _balances lock,
        // but once a SecureObservableLong is registered it is never removed, so reading
        // the reference outside the lock is safe for existing entries.
        public SecureObservableLong GetObservableBalance(string currencyId)
        {
            if (string.IsNullOrEmpty(currencyId)) return null;
            if (_balances.TryGetValue(currencyId, out var existing))
                return existing;

            lock (_balances)
            {
                return LazyLoadBalance(currencyId);
            }
        }

        // Must be called under _balances lock.
        private SecureObservableLong LazyLoadBalance(string currencyId)
        {
            if (_balances.TryGetValue(currencyId, out var prop))
                return prop;

            long savedAmount = PlayerPrefsService != null
                ? PlayerPrefsService.GetLong($"NT_Eco_{currencyId}", 0L)
                : 0L;
            prop = new SecureObservableLong(savedAmount);
            _balances[currencyId] = prop;
            return prop;
        }

        private void SaveBalance(string currencyId, long amount)
        {
            PlayerPrefsService?.SetLong($"NT_Eco_{currencyId}", amount);
        }

        /// <summary>
        /// Awaits the network spend validation and, if the server REJECTS the spend,
        /// restores the optimistically-deducted amount (bounded at <see cref="long.MaxValue"/>).
        /// Fire-and-forget on purpose: the local commit must never block the caller.
        /// </summary>
        private async Task ReconcileSpendAsync(string currencyId, long amount, string reason)
        {
            bool approved;
            try
            {
                approved = await NetworkValidator.ValidateSpendAsync(currencyId, amount, reason);
            }
            catch (Exception ex)
            {
                // Network failure: keep the optimistic balance; a later reconciliation (or the
                // server's authoritative ledger) settles the difference. Surface for debugging.
                NexusRuntime.Logger?.LogWarning($"[Economy] Spend validation failed for '{currencyId}': {ex.Message}");
                return;
            }

            if (!approved)
            {
                lock (_balances)
                {
                    var prop = GetObservableBalance(currencyId);
                    prop.Value = Math.Min(prop.Value + amount, long.MaxValue);
                    SaveBalance(currencyId, prop.Value);
                }
                NexusRuntime.Logger?.LogWarning($"[Economy] Server rejected spend of {amount} '{currencyId}' — balance restored.");
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

        public override void Dispose()
        {
            lock (_balances)
            {
                foreach (var kvp in _balances)
                {
                    kvp.Value.ClearOnChanged();
                }
                _balances.Clear();
            }
        }
    }
}
