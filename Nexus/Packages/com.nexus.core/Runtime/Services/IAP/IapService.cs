using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public enum ProductType
    {
        Consumable,
        NonConsumable,
        Subscription
    }

    public class ProductDefinition
    {
        public string Id { get; set; }
        public ProductType Type { get; set; }
        /// <summary>The catalog price shown until the store reports its localized one; null (the default) shows none (#163).</summary>
        public string PriceString { get; set; }
    }

    public interface IIapStoreAdapter
    {
        void Initialize(List<ProductDefinition> products, Action<bool> onInitialized);
        void Purchase(string productId, Action<bool, string> onComplete);
        void Restore(Action<bool> onComplete);
        bool IsOwned(string productId);
    }

    public interface IIapService
    {
        void SetStoreAdapter(IIapStoreAdapter adapter);
        void RegisterProducts(params ProductDefinition[] products);
        void PurchaseProduct(string productId, Action<bool, string> onComplete);
        void RestorePurchases(Action<bool> onComplete);
        bool IsProductOwned(string productId);
        ProductDefinition GetProduct(string productId);

        /// <summary>Purchase with a structured result; bool-only adapters are mapped to Purchased, Unavailable or Failed.</summary>
        void Purchase(string productId, Action<PurchaseOutcome> onOutcome);
        /// <summary>The store's localized price when the adapter reports one, else the catalog's <see cref="ProductDefinition.PriceString"/>, else null.</summary>
        string GetLocalizedPrice(string productId);
        /// <summary>True when purchases can be attempted now: the adapter reports availability; without an adapter, false in every build.</summary>
        bool IsStoreAvailable { get; }
        /// <summary>Raised when the adapter reports a store change (connected, products or purchases fetched, restore).</summary>
        event Action StoreChanged;
    }

    [Preserve]
    public class IapService : NexusService<IIapService>, IIapService
    {
        // Guards adapter swap and read-use hand-off. The catalog has its own lock;
        // the adapter is a separate mutable reference that SetStoreAdapter/Purchase/
        // Restore/Ownership/OnDispose touch from potentially different threads, so it
        // needs its own discipline (mirrors AdService's _lock pattern).
        private readonly object _adapterLock = new();
        private IIapStoreAdapter _adapter;
        public event Action StoreChanged;
        private readonly Action _forwardStoreChanged;
        private readonly Dictionary<string, ProductDefinition> _catalog = new();

        public IapService()
        {
            _forwardStoreChanged = () => StoreChanged?.Invoke();
        }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void SetStoreAdapter(IIapStoreAdapter adapter)
        {
            IIapStoreAdapter toInitialize;
            List<ProductDefinition> productList;
            lock (_catalog)
            {
                productList = new List<ProductDefinition>(_catalog.Values);
            }
            IIapStoreAdapter previous;
            lock (_adapterLock)
            {
                previous = _adapter;
                _adapter = adapter;
                toInitialize = adapter;
            }
            if (previous is IIapStoreInfoAdapter previousInfo) previousInfo.Changed -= _forwardStoreChanged;
            if (adapter is IIapStoreInfoAdapter info) info.Changed += _forwardStoreChanged;
            // Initialize OUTSIDE the lock: store SDKs may block or re-enter this service
            // (e.g. invoke the callback synchronously), and holding the lock across that
            // would deadlock any other caller.
            toInitialize?.Initialize(productList, (success) =>
            {
                NexusRuntime.Logger?.Log($"[IapService] Store adapter initialized: {success}");
            });
        }

        public void RegisterProducts(params ProductDefinition[] products)
        {
            lock (_catalog)
            {
                foreach (var p in products)
                {
                    if (p != null && !string.IsNullOrEmpty(p.Id))
                    {
                        _catalog[p.Id] = p;
                    }
                }
            }
        }

        public void Purchase(string productId, Action<PurchaseOutcome> onOutcome)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter is IIapOutcomeAdapter outcomes)
            {
                outcomes.PurchaseWithOutcome(productId, onOutcome);
                return;
            }
            // Bool-only adapters and no adapter: a failure code names the store state, everything else failed.
            PurchaseProduct(productId, (success, info) => onOutcome?.Invoke(success ? PurchaseOutcome.Purchased
                : info == "store_unavailable" || info == "not_initialized" ? PurchaseOutcome.Unavailable
                : PurchaseOutcome.Failed));
        }

        public string GetLocalizedPrice(string productId)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter is IIapStoreInfoAdapter info)
            {
                var price = info.GetLocalizedPrice(productId);
                if (!string.IsNullOrEmpty(price)) return price;
            }
            return GetProduct(productId)?.PriceString;
        }

        public bool IsStoreAvailable
        {
            get
            {
                IIapStoreAdapter adapter;
                lock (_adapterLock) { adapter = _adapter; }
                if (adapter is IIapStoreInfoAdapter info) return info.IsAvailable;
                // No store, no purchase: the editor too goes through a real adapter (Unity IAP's editor store), #162.
                return adapter != null;
            }
        }

        public void PurchaseProduct(string productId, Action<bool, string> onComplete)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter != null)
            {
                adapter.Purchase(productId, onComplete);
                return;
            }

            // No adapter: never throw and never simulate (#162). The caller gets store_unavailable, which its UI shows as
            // "store unavailable", in the editor and in every player, and the warning names what is missing.
            var logger = NexusRuntime.Logger;
            logger?.LogWarning(
                $"[IapService] PurchaseProduct('{productId}') called without a store adapter: answered store_unavailable. " +
                "Set the store with SetStoreAdapter (com.nexus.iap.unity's UnityIapAdapter, which also runs Unity IAP's editor store) " +
                "before any UI can start a purchase.");
            try
            {
                onComplete?.Invoke(false, "store_unavailable");
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
        }

        public void RestorePurchases(Action<bool> onComplete)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            if (adapter != null)
            {
                adapter.Restore(onComplete);
                return;
            }

            // The same answer for a restore: nothing restored, and the warning names the missing store (#162).
            var logger = NexusRuntime.Logger;
            logger?.LogWarning("[IapService] RestorePurchases called without a store adapter: answered false. Set the store with SetStoreAdapter first.");
            try
            {
                onComplete?.Invoke(false);
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
        }

        public bool IsProductOwned(string productId)
        {
            IIapStoreAdapter adapter;
            lock (_adapterLock) { adapter = _adapter; }
            // Ownership comes only from a store; without one nothing is owned (#162).
            return adapter != null && adapter.IsOwned(productId);
        }

        public ProductDefinition GetProduct(string productId)
        {
            lock (_catalog)
            {
                _catalog.TryGetValue(productId, out var p);
                return p;
            }
        }

        public override void OnDispose()
        {
            // Release the platform adapter so its native handles (billing connections) are
            // torn down deterministically instead of leaking until process exit. Swap under
            // the lock so a concurrent Purchase/Restore/Ownership either captures the
            // adapter before disposal or observes null (graceful "store unavailable" path).
            IIapStoreAdapter adapter;
            lock (_adapterLock)
            {
                adapter = _adapter;
                _adapter = null;
            }
            if (adapter is IIapStoreInfoAdapter info) info.Changed -= _forwardStoreChanged;
            if (adapter is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
