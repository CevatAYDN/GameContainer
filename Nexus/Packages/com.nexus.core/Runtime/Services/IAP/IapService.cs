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
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.Log($"[IapService] Store adapter initialized: {success}");
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
            }
            else
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.Log($"[IapService Mock] Purchased product: {productId}");
                lock (_catalog)
                {
                    _mockOwnedProducts.Add(productId);
                }
                onComplete?.Invoke(true, productId);
#else
                throw new InvalidOperationException("[IapService] Cannot purchase product: Store adapter is not initialized in production builds!");
#endif
            }
        }

        public void RestorePurchases(Action<bool> onComplete)
        {
            if (_adapter != null)
            {
                _adapter.Restore(onComplete);
            }
            else
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.Log("[IapService Mock] Restored purchases");
                onComplete?.Invoke(true);
#else
                throw new InvalidOperationException("[IapService] Cannot restore purchases: Store adapter is not initialized in production builds!");
#endif
            }
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
