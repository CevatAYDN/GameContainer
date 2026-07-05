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

    [Preserve]
    public class EconomyService : IEconomyService, INexusService, IDisposable
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        private readonly Dictionary<string, ObservableProperty<long>> _balances = new();

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public ObservableProperty<long> GetObservableBalance(string currencyId)
        {
            if (string.IsNullOrEmpty(currencyId)) return null;

            if (!_balances.TryGetValue(currencyId, out var prop))
            {
                long savedAmount = PlayerPrefsService != null ? (long)PlayerPrefsService.GetFloat($"NT_Eco_{currencyId}", 0f) : 0L;
                prop = new ObservableProperty<long>(savedAmount);
                _balances[currencyId] = prop;
            }

            return prop;
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
            var prop = GetObservableBalance(currencyId);
            if (prop.Value < amount) return false;

            prop.Value -= amount;
            SaveBalance(currencyId, prop.Value);
            return true;
        }

        public void Earn(string currencyId, long amount, string reason = "")
        {
            if (amount <= 0) return;
            var prop = GetObservableBalance(currencyId);
            prop.Value += amount;
            SaveBalance(currencyId, prop.Value);
        }

        public void SetBalance(string currencyId, long amount)
        {
            var prop = GetObservableBalance(currencyId);
            prop.Value = Math.Max(0L, amount);
            SaveBalance(currencyId, prop.Value);
        }

        private void SaveBalance(string currencyId, long amount)
        {
            PlayerPrefsService?.SetFloat($"NT_Eco_{currencyId}", (float)amount);
        }

        public void OnDispose() => Dispose();

        public void Dispose()
        {
            foreach (var kvp in _balances)
            {
                kvp.Value.ClearOnChanged();
            }
            _balances.Clear();
        }
    }
}
