using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
            var productList = new List<ProductDefinition>(_catalog.Values);
            _adapter?.Initialize(productList, (success) =>
            {
                Debug.Log($"[IapService] Store adapter initialized: {success}");
            });
        }

        public void RegisterProducts(params ProductDefinition[] products)
        {
            foreach (var p in products)
            {
                if (p != null && !string.IsNullOrEmpty(p.Id))
                {
                    _catalog[p.Id] = p;
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
                Debug.Log($"[IapService Mock] Purchased product: {productId}");
                _mockOwnedProducts.Add(productId);
                onComplete?.Invoke(true, productId);
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
                Debug.Log("[IapService Mock] Restored purchases");
                onComplete?.Invoke(true);
            }
        }

        public bool IsProductOwned(string productId)
        {
            if (_adapter != null) return _adapter.IsOwned(productId);
            return _mockOwnedProducts.Contains(productId);
        }

        public ProductDefinition GetProduct(string productId)
        {
            _catalog.TryGetValue(productId, out var p);
            return p;
        }

        public void OnDispose() { }
    }
}
