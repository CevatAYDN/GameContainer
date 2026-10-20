#if NEXUS_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Purchasing;
using NexusProduct = Nexus.Core.Services.ProductDefinition;
using NexusProductType = Nexus.Core.Services.ProductType;
using StoreProduct = UnityEngine.Purchasing.ProductDefinition;
using StoreProductType = UnityEngine.Purchasing.ProductType;

[assembly: InternalsVisibleTo("com.nexus.iap.unity.tests")]

namespace Nexus.Iap.Unity
{
    /// <summary>
    /// Unity IAP 5 behind <see cref="IapService"/>. The store connects at <see cref="Initialize"/>,
    /// fetches the catalog's products and the purchases this account already owns, and confirms every
    /// pending order after the game was told about it. Prices are the store's localized strings.
    /// Ported from the first shipped Nexus game's store.
    /// </summary>
    public sealed class UnityIapAdapter : IIapStoreAdapter, IIapOutcomeAdapter, IIapStoreInfoAdapter
    {
        public const string ProviderName = "unity";

        /// <summary>Registers the adapter with the factory under <see cref="ProviderName"/>.</summary>
        public static void Register(IIapAdapterFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            factory.RegisterProvider(ProviderName, () => new UnityIapAdapter());
        }

        private StoreController _store;
        private bool _connected, _connecting, _productsFetched;
        private readonly Dictionary<string, NexusProductType> _catalog = new();
        private readonly HashSet<string> _owned = new();
        private readonly HashSet<string> _deferred = new();
        private Action<PurchaseOutcome> _pending;
        private Action<bool> _restorePending;
        private Action<bool> _initializePending;

        public event Action Changed;
        public bool IsAvailable => _connected && _productsFetched;

        public string GetLocalizedPrice(string productId) => _store?.GetProductById(productId)?.metadata?.localizedPriceString;
        public bool IsOwned(string productId) => _owned.Contains(productId);

        public async void Initialize(List<NexusProduct> products, Action<bool> onInitialized)
        {
            _catalog.Clear();
            var definitions = new List<StoreProduct>();
            foreach (var product in products ?? new List<NexusProduct>())
            {
                if (string.IsNullOrEmpty(product?.Id)) continue;
                _catalog[product.Id] = product.Type;
                definitions.Add(new StoreProduct(product.Id, Map(product.Type)));
            }
            if (_connecting) { onInitialized?.Invoke(false); return; }
            _initializePending = onInitialized;
            if (_store == null)
            {
                _store = UnityIAPServices.StoreController();
                _store.OnPurchasePending += OnPending;
                _store.OnPurchaseFailed += OnFailed;
                _store.OnPurchaseDeferred += order => { Defer(order, true); Complete(PurchaseOutcome.Pending); };
                _store.OnPurchasesFetched += OnFetched;
                _store.OnPurchasesFetchFailed += failure => { Warn(Describe(failure)); FinishRestore(false); };
                _store.OnProductsFetched += _ => { _productsFetched = true; _store.FetchPurchases(); FinishInitialize(true); Changed?.Invoke(); };
                _store.OnProductsFetchFailed += failure => { Warn(Describe(failure)); FinishInitialize(false); Changed?.Invoke(); };
                _store.OnStoreDisconnected += failure => { _connected = false; Warn("Disconnected: " + failure?.Message); Changed?.Invoke(); };
            }
            _connecting = true;
            try
            {
                if (!_connected) await _store.Connect();
                _connected = true;
            }
            catch (Exception e)
            {
                Warn("The store is not reachable: " + e.Message);
                FinishInitialize(false);
                Changed?.Invoke();
                return;
            }
            finally { _connecting = false; }
            if (definitions.Count == 0) { _productsFetched = true; FinishInitialize(true); Changed?.Invoke(); return; }
            _store.FetchProducts(definitions);
        }

        public void Purchase(string productId, Action<bool, string> onComplete)
            => PurchaseWithOutcome(productId, outcome => onComplete?.Invoke(outcome == PurchaseOutcome.Purchased,
                outcome == PurchaseOutcome.Purchased ? productId : outcome == PurchaseOutcome.Unavailable ? "store_unavailable" : outcome.ToString().ToLowerInvariant()));

        public void PurchaseWithOutcome(string productId, Action<PurchaseOutcome> onOutcome)
        {
            // a payment the store holds (deferred) answers Pending: a second store call failed as "already owned", read as "not charged"
            if (productId != null && _deferred.Contains(productId)) { onOutcome?.Invoke(PurchaseOutcome.Pending); return; }
            if (!IsAvailable || string.IsNullOrEmpty(productId) || !_catalog.ContainsKey(productId)) { onOutcome?.Invoke(PurchaseOutcome.Unavailable); return; }
            var product = _store.GetProductById(productId);
            if (product == null || !product.availableToPurchase) { onOutcome?.Invoke(PurchaseOutcome.Unavailable); return; }
            if (IsOwned(productId)) { onOutcome?.Invoke(PurchaseOutcome.Purchased); return; }
            if (_pending != null) { onOutcome?.Invoke(PurchaseOutcome.Pending); return; }
            _pending = onOutcome;
            _store.PurchaseProduct(productId);
        }

        public void Restore(Action<bool> onComplete)
        {
            if (!_connected) { onComplete?.Invoke(false); return; }
            if (_restorePending != null) { onComplete?.Invoke(false); return; }
            _restorePending = onComplete;
            _store.RestoreTransactions((ok, message) =>
            {
                if (!ok) { Warn("Restore: " + message); FinishRestore(false); return; }
                _store.FetchPurchases();
            });
        }

        private void OnPending(PendingOrder order)
        {
            Own(order);
            // The game records ownership before the order is acknowledged with the store.
            Complete(PurchaseOutcome.Purchased);
            Changed?.Invoke();
            _store.ConfirmPurchase(order);
        }

        private void OnFetched(Orders orders)
        {
            _deferred.Clear();   // a deferral the player cancelled is gone from the next fetch
            foreach (var order in orders.DeferredOrders) Defer(order, true);
            foreach (var order in orders.ConfirmedOrders) Own(order);
            foreach (var order in orders.PendingOrders) Own(order);
            Changed?.Invoke();
            foreach (var order in orders.PendingOrders) _store.ConfirmPurchase(order);
            FinishRestore(true);
        }

        private void OnFailed(FailedOrder failed)
        {
            Warn($"Purchase failed: {failed.FailureReason} {failed.Details}");
            Complete(failed.FailureReason == PurchaseFailureReason.UserCancelled ? PurchaseOutcome.Cancelled
                : failed.FailureReason == PurchaseFailureReason.ProductUnavailable || failed.FailureReason == PurchaseFailureReason.PurchasingUnavailable || failed.FailureReason == PurchaseFailureReason.StoreNotConnected ? PurchaseOutcome.Unavailable
                : PurchaseOutcome.Failed);
        }

        /// <summary>Consumables are never owned; non-consumables and subscriptions are owned once ordered.</summary>
        private void Own(Order order)
        {
            Defer(order, false);
            foreach (var item in order.CartOrdered.Items())
            {
                string id = item.Product?.definition?.id;
                if (string.IsNullOrEmpty(id)) continue;
                if (_catalog.TryGetValue(id, out var type) && type == NexusProductType.Consumable) continue;
                _owned.Add(id);
            }
        }

        private void Defer(Order order, bool deferred)
        {
            foreach (var item in order.CartOrdered.Items()) Defer(item.Product?.definition?.id, deferred);
        }

        /// <summary>Marks a product whose payment the store holds (true) or settled (false).</summary>
        internal void Defer(string productId, bool deferred)
        {
            if (string.IsNullOrEmpty(productId)) return;
            if (deferred) _deferred.Add(productId); else _deferred.Remove(productId);
        }

        private void Complete(PurchaseOutcome outcome) { var done = _pending; _pending = null; done?.Invoke(outcome); }
        private void FinishRestore(bool ok) { var done = _restorePending; _restorePending = null; done?.Invoke(ok); }
        private void FinishInitialize(bool ok) { var done = _initializePending; _initializePending = null; done?.Invoke(ok); }

        private static StoreProductType Map(NexusProductType type) => type switch
        {
            NexusProductType.Consumable => StoreProductType.Consumable,
            NexusProductType.Subscription => StoreProductType.Subscription,
            _ => StoreProductType.NonConsumable,
        };

        // Neither failure type overrides ToString; concatenating one logs only its type name.
        internal static string Describe(ProductFetchFailed failure)
        {
            var ids = new List<string>();
            foreach (var product in failure?.FailedFetchProducts ?? new List<StoreProduct>()) ids.Add(product?.id);
            return $"Products not fetched ({string.Join(", ", ids)}): {failure?.FailureReason}";
        }

        internal static string Describe(PurchasesFetchFailureDescription failure)
            => $"Purchases not fetched: {failure?.FailureReason} {failure?.Message}";

        private static void Warn(string message) => NexusLog.WarnAlways("[UnityIAP] " + message);
    }
}
#endif
