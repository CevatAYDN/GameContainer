using System;
using System.Collections.Generic;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Test double of a store: after Initialize every catalog product can be bought, a non-consumable or subscription is
    /// then owned and a consumable never is, as with the Unity IAP adapter (#230), and a restore succeeds. Testing kit only
    /// (editor and test builds); a player has no simulated store (#162).
    /// </summary>
    public sealed class MockIapStoreAdapter : IIapStoreAdapter, IIapOutcomeAdapter, IIapStoreInfoAdapter, IIapUnrequestedPurchaseAdapter
    {
        public event Action Changed;
        /// <summary>Raised by <see cref="ReportUnrequestedPurchase"/>.</summary>
        public event UnrequestedPurchaseHandler UnrequestedPurchase;

        /// <summary>Reports a paid consumable the game did not request, as a store's purchase fetch does (issue #204); false when
        /// nothing handles it, in which case a store keeps the order.</summary>
        public bool ReportUnrequestedPurchase(string productId, string transactionId)
        {
            var handler = UnrequestedPurchase;
            if (handler == null) return false;
            handler(productId, transactionId);
            return true;
        }
        public bool IsAvailable => _initialized;
        /// <summary>True after Initialize, unless the test keeps it unknown with <see cref="SetOwnershipKnown"/>.</summary>
        public bool OwnershipKnown => _initialized && _ownershipAnswered;
        private bool _ownershipAnswered = true;

        /// <summary>Answers ownership (the default) or keeps it unknown, as a store does offline or before its purchase fetch.</summary>
        public void SetOwnershipKnown(bool known)
        {
            _ownershipAnswered = known;
            Changed?.Invoke();
        }

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

            if (product.Type != ProductType.Consumable) _ownedProducts.Add(productId);
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
