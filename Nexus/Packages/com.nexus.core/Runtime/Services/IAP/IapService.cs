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
        public string PriceString { get; set; } = "$0.99";
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
    }

    [Preserve]
    [StubService("Replace with Unity IAP / RevenueCat adapter before release")]
    public class IapService : IIapService, INexusService
    {
        private IIapStoreAdapter _adapter;
        private readonly Dictionary<string, ProductDefinition> _catalog = new();
        private readonly HashSet<string> _mockOwnedProducts = new();

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void SetStoreAdapter(IIapStoreAdapter adapter)
        {
            _adapter = adapter;
            List<ProductDefinition> productList;
            lock (_catalog)
            {
                productList = new List<ProductDefinition>(_catalog.Values);
            }
            if (_adapter != null)
            {
                _adapter.Initialize(productList, (success) =>
                {
                    NexusRuntime.Logger?.Log($"[IapService] Store adapter initialized: {success}");
                });
            }
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

        public void PurchaseProduct(string productId, Action<bool, string> onComplete)
        {
            if (_adapter != null)
            {
                _adapter.Purchase(productId, onComplete);
                return;
            }

            // No adapter bound — fall back gracefully instead of throwing.
            // FIX P0.2: previously this threw InvalidOperationException in release builds,
            // crashing the app when the player tapped "Remove Ads" before the platform
            // adapter had finished initialising (cold start, missing network entitlement, etc.).
            var logger = NexusRuntime.Logger;
            if (logger != null)
            {
                logger.LogException(new InvalidOperationException(
                    $"[IapService] PurchaseProduct('{productId}') called without an IStoreAdapter bound. " +
                    "The Real Store Adapter is owned by the platform bootstrapper; if this is a release build, " +
                    "verify that GameplayLifecycle registers the adapter before any UI can trigger a purchase."));
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Developer/Editor: simulate a successful purchase so QA flows remain testable.
            lock (_catalog)
            {
                _mockOwnedProducts.Add(productId);
            }
            onComplete?.Invoke(true, productId);
#else
            // FIX P0.2 (release): never throw. Surface a localised, actionable failure
            // through the existing callback contract so the caller can display a
            // "Store temporarily unavailable" toast and queue the purchase intent for retry.
            try
            {
                onComplete?.Invoke(false, "store_unavailable");
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
#endif
        }

        public void RestorePurchases(Action<bool> onComplete)
        {
            if (_adapter != null)
            {
                _adapter.Restore(onComplete);
                return;
            }

            // FIX P0.2 (mirror): same graceful failure for restore flow.
            var logger = NexusRuntime.Logger;
            if (logger != null)
            {
                logger.LogException(new InvalidOperationException(
                    "[IapService] RestorePurchases called without an IStoreAdapter bound."));
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            onComplete?.Invoke(true);
#else
            try
            {
                onComplete?.Invoke(false);
            }
            catch (Exception ex)
            {
                logger?.LogException(ex);
            }
#endif
        }

        public bool IsProductOwned(string productId)
        {
            if (_adapter != null) return _adapter.IsOwned(productId);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            lock (_catalog)
            {
                return _mockOwnedProducts.Contains(productId);
            }
#else
            return false;
#endif
        }

        public ProductDefinition GetProduct(string productId)
        {
            lock (_catalog)
            {
                _catalog.TryGetValue(productId, out var p);
                return p;
            }
        }

        public void OnDispose() { }
    }
}
