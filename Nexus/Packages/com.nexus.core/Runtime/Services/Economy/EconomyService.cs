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
        ObservableProperty<long> GetObservableBalance(string currencyId);
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

        private readonly Dictionary<string, ObservableProperty<long>> _balances = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public ObservableProperty<long> GetObservableBalance(string currencyId)
        {
            if (string.IsNullOrEmpty(currencyId)) return null;

            lock (_balances)
            {
                if (!_balances.TryGetValue(currencyId, out var prop))
                {
                    long savedAmount = PlayerPrefsService != null ? PlayerPrefsService.GetLong($"NT_Eco_{currencyId}", 0L) : 0L;
                    prop = new ObservableProperty<long>(savedAmount);
                    _balances[currencyId] = prop;
                }

                return prop;
            }
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

            lock (_balances)
            {
                var prop = GetObservableBalance(currencyId);
                if (prop.Value < amount) return false;

                prop.Value -= amount;
                SaveBalance(currencyId, prop.Value);

                if (NetworkValidator != null)
                {
                    _ = NetworkValidator.ValidateSpendAsync(currencyId, amount, reason);
                }
                return true;
            }
        }

        public void Earn(string currencyId, long amount, string reason = "")
        {
            if (amount <= 0) return;

            lock (_balances)
            {
                var prop = GetObservableBalance(currencyId);
                prop.Value += amount;
                SaveBalance(currencyId, prop.Value);

                if (NetworkValidator != null)
                {
                    _ = NetworkValidator.ValidateEarnAsync(currencyId, amount, reason);
                }
            }
        }

        public void SetBalance(string currencyId, long amount)
        {
            lock (_balances)
            {
                var prop = GetObservableBalance(currencyId);
                prop.Value = Math.Max(0L, amount);
                SaveBalance(currencyId, prop.Value);
            }
        }

        private void SaveBalance(string currencyId, long amount)
        {
            PlayerPrefsService?.SetLong($"NT_Eco_{currencyId}", amount);
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
