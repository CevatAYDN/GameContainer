using System;
using System.Collections.Generic;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Test double of a store: after Initialize every catalog product can be bought and is then owned, and a restore
    /// succeeds. Testing kit only (editor and test builds); a player has no simulated store (#162).
    /// </summary>
    public sealed class MockIapStoreAdapter : IIapStoreAdapter, IIapOutcomeAdapter, IIapStoreInfoAdapter
    {
        public event Action Changed;
        public bool IsAvailable => _initialized;

        /// <summary>The catalog's price string stands in for the store's localized price.</summary>
        public string GetLocalizedPrice(string productId) => _catalog.Find(p => p.Id == productId)?.PriceString;

        public void PurchaseWithOutcome(string productId, Action<PurchaseOutcome> onOutcome)
        {
            Purchase(productId, (success, info) => onOutcome?.Invoke(success ? PurchaseOutcome.Purchased
                : info == "store_unavailable" || info == "not_initialized" ? PurchaseOutcome.Unavailable
                : PurchaseOutcome.Failed));
        }

        private readonly HashSet<string> _ownedProducts = new();
        private bool _initialized;
        private List<ProductDefinition> _catalog = new();

        public void Initialize(List<ProductDefinition> products, Action<bool> onInitialized)
        {
            _catalog = products ?? new List<ProductDefinition>();
            _initialized = true;
            onInitialized?.Invoke(true);
            Changed?.Invoke();
        }

        public void Purchase(string productId, Action<bool, string> onComplete)
        {
            if (!_initialized)
            {
                onComplete?.Invoke(false, "not_initialized");
                return;
            }

            var product = _catalog.Find(p => p.Id == productId);
            if (product == null)
            {
                onComplete?.Invoke(false, "product_not_found");
                return;
            }

            _ownedProducts.Add(productId);
            onComplete?.Invoke(true, productId);
        }

        public void Restore(Action<bool> onComplete)
        {
            if (!_initialized)
            {
                onComplete?.Invoke(false);
                return;
            }
            onComplete?.Invoke(true);
        }

        public bool IsOwned(string productId) => _ownedProducts.Contains(productId);

        /// <summary>Manually set product ownership for testing scenarios.</summary>
        public void SetOwned(string productId, bool owned)
        {
            if (owned) _ownedProducts.Add(productId);
            else _ownedProducts.Remove(productId);
            Changed?.Invoke();
        }
    }
}
