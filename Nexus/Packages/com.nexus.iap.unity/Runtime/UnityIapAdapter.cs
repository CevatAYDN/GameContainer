#if NEXUS_UNITY_IAP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Purchasing;
using NexusProduct = Nexus.Core.Services.ProductDefinition;
using NexusProductType = Nexus.Core.Services.ProductType;
using StoreProduct = UnityEngine.Purchasing.ProductDefinition;
using StoreProductType = UnityEngine.Purchasing.ProductType;

[assembly: InternalsVisibleTo("com.nexus.iap.unity.tests")]
[assembly: InternalsVisibleTo("com.nexus.iap.unity.runtime.tests")]

namespace Nexus.Iap.Unity
{
    /// <summary>
    /// Unity IAP 5 behind <see cref="IapService"/>. The store connects at <see cref="Initialize"/>,
    /// fetches the catalog's products and the purchases this account already owns, and confirms every
    /// pending order after the game was told about it. Prices are the store's localized strings.
    /// Unity IAP's store services are process-wide, so a replaced adapter must be disposed: it removes its handlers
    /// (<see cref="IapService.SetStoreAdapter"/> disposes the adapter it replaces). Ported from the first shipped Nexus game's store.
    /// </summary>
    public sealed class UnityIapAdapter : IIapStoreAdapter, IIapOutcomeAdapter, IIapStoreInfoAdapter, IIapUnrequestedPurchaseAdapter, IDisposable
    {
        public const string ProviderName = "unity";

        /// <summary>Registers the adapter with the factory under <see cref="ProviderName"/>.</summary>
        public static void Register(IIapAdapterFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            factory.RegisterProvider(ProviderName, () => new UnityIapAdapter());
        }

        private StoreController _store;
        private bool _connected, _connecting, _productsFetched, _disposed;
        private readonly Dictionary<string, NexusProductType> _catalog = new();
        private readonly HashSet<string> _owned = new();
        private readonly HashSet<string> _deferred = new();
        private Action<PurchaseOutcome> _pending;
        private string _pendingProductId;   // the product _pending waits for: an order for another product does not answer it (issue #182)
        private Action<bool> _restorePending;
        private Action<bool> _initializePending;
        // Orders granted to the player and sent for confirmation, by transaction id, until the store confirms them (issue #172)
        private readonly Dictionary<string, PendingOrder> _unconfirmed = new();
        private readonly Dictionary<string, int> _confirmAttempts = new();

        /// <summary>The wait before each new confirm of an order the store refused. After the last one the adapter warns and leaves
        /// the order to the next purchase fetch (each start and <see cref="Restore"/>).</summary>
        internal TimeSpan[] ConfirmRetryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) };

        public event Action Changed;
        /// <summary>A paid consumable the game did not request in this session; see <see cref="IIapUnrequestedPurchaseAdapter"/>.</summary>
        public event UnrequestedPurchaseHandler UnrequestedPurchase;
        private readonly HashSet<string> _keptUnrequested = new();   // orders left with the store for want of a handler, warned once
        private readonly HashSet<string> _keptUnsafeCarts = new();
        public bool IsAvailable => _connected && _productsFetched;
        /// <summary>True once a purchase fetch answered (each connected start, also one whose product fetch failed (#268), and
        /// <see cref="Restore"/>); a failed fetch leaves it as it was (#183).</summary>
        public bool OwnershipKnown { get; private set; }

        public string GetLocalizedPrice(string productId) => _store?.GetProductById(productId)?.metadata?.localizedPriceString;
        public bool IsOwned(string productId) => _owned.Contains(productId);

        /// <summary>True while a purchase of the product is granted but not yet confirmed (acknowledged) with the store. Google Play
        /// refunds a purchase not acknowledged within three days. <see cref="Changed"/> is raised when it becomes false.</summary>
        public bool IsConfirmationPending(string productId)
        {
            foreach (var order in _unconfirmed.Values)
                foreach (var item in order.CartOrdered.Items())
                    if (item.Product?.definition?.id == productId) return true;
            return false;
        }

        public async void Initialize(List<NexusProduct> products, Action<bool> onInitialized) // NEXUS002-exempt: IIapStoreAdapter.Initialize returns void and answers through onInitialized; the store calls are caught
        {
            if (_disposed) { onInitialized?.Invoke(false); return; }
            _catalog.Clear();
            _productsFetched = false;   // the new catalog is available only once the store fetched it (issue #118)
            var definitions = new List<StoreProduct>();
            foreach (var product in products ?? new List<NexusProduct>())
            {
                if (string.IsNullOrEmpty(product?.Id)) continue;
                _catalog[product.Id] = product.Type;
                definitions.Add(new StoreProduct(product.Id, Map(product.Type)));
            }
            if (_connecting) { onInitialized?.Invoke(false); return; }
            _initializePending = onInitialized;
            _connecting = true;
            // the store's creation is inside the try: in this async void a throw there went unobserved and the caller was never answered
            try
            {
                if (_store == null) _store = CreateStore();
                if (!_connected) await _store.Connect();
            }
            catch (Exception e)
            {
                Warn("The store is not reachable: " + e.Message);
                FinishInitialize(false);
                Changed?.Invoke();
                return;
            }
            finally { _connecting = false; }
            if (_disposed) { FinishInitialize(false); return; }
            // Unity IAP completes Connect's task on a failed connect too, after OnStoreDisconnected (which warned): only
            // OnStoreConnected says the store connected (issue #118)
            if (!_connected) { FinishInitialize(false); Changed?.Invoke(); return; }
            if (definitions.Count == 0) { _productsFetched = true; OwnershipKnown = true; FinishInitialize(true); Changed?.Invoke(); return; }   // nothing to own
            _store.FetchProducts(definitions);
        }

        private StoreController CreateStore()
        {
            var store = UnityIAPServices.StoreController();
            store.OnStoreConnected += OnStoreConnected;
            store.OnStoreDisconnected += OnStoreDisconnected;
            store.OnPurchasePending += OnPending;
            store.OnPurchaseConfirmed += OnConfirmed;
            store.OnPurchaseFailed += OnFailed;
            store.OnPurchaseDeferred += OnDeferred;
            store.OnPurchasesFetched += OnFetched;
            store.OnPurchasesFetchFailed += OnPurchasesFetchFailed;
            store.OnProductsFetched += OnProductsFetched;
            store.OnProductsFetchFailed += OnProductsFetchFailed;
            return store;
        }

        /// <summary>Removes the adapter's handlers from Unity IAP's process-wide store services and answers what waits: a purchase
        /// <see cref="PurchaseOutcome.Unavailable"/>, a restore and a start false (issue #118).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var store = _store;
            _store = null;
            if (store != null)
            {
                store.OnStoreConnected -= OnStoreConnected;
                store.OnStoreDisconnected -= OnStoreDisconnected;
                store.OnPurchasePending -= OnPending;
                store.OnPurchaseConfirmed -= OnConfirmed;
                store.OnPurchaseFailed -= OnFailed;
                store.OnPurchaseDeferred -= OnDeferred;
                store.OnPurchasesFetched -= OnFetched;
                store.OnPurchasesFetchFailed -= OnPurchasesFetchFailed;
                store.OnProductsFetched -= OnProductsFetched;
                store.OnProductsFetchFailed -= OnProductsFetchFailed;
            }
            _connected = _productsFetched = false;
            Complete(PurchaseOutcome.Unavailable);
            FinishRestore(false);
            FinishInitialize(false);
        }

        private void OnStoreConnected() { _connected = true; Changed?.Invoke(); }
        private void OnStoreDisconnected(StoreConnectionFailureDescription failure) { _connected = false; Warn("Disconnected: " + failure?.Message); Changed?.Invoke(); }
        private void OnDeferred(DeferredOrder order) { Defer(order, true); if (Answers(order)) Complete(PurchaseOutcome.Pending); }
        private void OnPurchasesFetchFailed(PurchasesFetchFailureDescription failure) { Warn(Describe(failure)); FinishRestore(false); }
        private void OnProductsFetched(List<Product> products) { _productsFetched = true; _store.FetchPurchases(); FinishInitialize(true); Changed?.Invoke(); }
        // Unity IAP fetches purchases without the products (Google Play falls back to an unknown product with the order's id), so
        // ownership becomes known while purchasing stays unavailable (#268)
        private void OnProductsFetchFailed(ProductFetchFailed failure) { _productsFetched = false; Warn(Describe(failure)); _store.FetchPurchases(); FinishInitialize(false); Changed?.Invoke(); }

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
            _pendingProductId = productId;
            _store.PurchaseProduct(productId);
        }

        public void Restore(Action<bool> onComplete)
        {
            if (!_connected) { onComplete?.Invoke(false); return; }
            if (_restorePending != null) { onComplete?.Invoke(false); return; }
            _restorePending = onComplete;
            // A successful restore fetches the purchases itself (Unity IAP's PurchaseService.RestoreTransactions, since 5.0.2), and
            // that fetch answers the restore; a second fetch here confirmed every pending order twice (issue #182).
            _store.RestoreTransactions((ok, message) =>
            {
                if (!ok) { Warn("Restore: " + message); FinishRestore(false); }
            });
        }

        private void OnPending(PendingOrder order)
        {
            if (!CanFinishConsumables(order))
            {
                if (Answers(order)) Complete(PurchaseOutcome.Failed);
                Changed?.Invoke();
                return;
            }
            Own(order);
            // An order for another product (a deferred payment approved, a promo code, and on Google Play and the App Store every
            // pending order a fetch finds, which Unity IAP routes here first) is not the waiting request's answer: an owned product is
            // reported through Changed, a consumable through UnrequestedPurchase, and without a handler the order is kept (#204).
            bool answers = Answers(order);
            if (!answers && !IsTracked(order) && !GrantUnrequested(order)) { Changed?.Invoke(); return; }
            Track(order);   // before Complete: the game told Purchased sees the purchase as not yet confirmed
            // The game records ownership before the order is acknowledged with the store.
            if (answers) Complete(PurchaseOutcome.Purchased);
            Changed?.Invoke();
            Confirm(order);
        }

        private void OnFetched(Orders orders)
        {
            _deferred.Clear();   // a deferral the player cancelled is gone from the next fetch
            foreach (var order in orders.DeferredOrders) Defer(order, true);
            foreach (var order in orders.ConfirmedOrders) Own(order);
            var confirm = new List<PendingOrder>();
            foreach (var order in orders.PendingOrders)
            {
                if (!CanFinishConsumables(order)) continue;
                Own(order);
                // an order granted in this session (its confirm is being retried, or OnPending just took it) is not granted again
                if (!IsTracked(order) && !GrantUnrequested(order)) continue;
                Track(order);
                confirm.Add(order);
            }
            OwnershipKnown = true;
            Changed?.Invoke();
            foreach (var order in confirm) Confirm(order);
            FinishRestore(true);
        }

        private bool IsTracked(PendingOrder order)
        {
            string id = order?.Info?.TransactionID;
            return !string.IsNullOrEmpty(id) && _unconfirmed.ContainsKey(id);
        }

        /// <summary>Hands the consumables of an order the game did not request to <see cref="UnrequestedPurchase"/>; true when the order
        /// may be confirmed. Confirming it with no handler consumed a paid purchase and granted nothing (issue #204).</summary>
        private bool GrantUnrequested(PendingOrder order)
        {
            if (!CanFinishConsumables(order)) return false;
            var consumables = new List<string>();
            string untyped = null;
            foreach (var item in order.CartOrdered.Items())
            {
                string id = item.Product?.definition?.id;
                if (string.IsNullOrEmpty(id)) continue;
                var type = TypeOf(item);
                if (type == StoreProductType.Consumable) consumables.Add(id);
                else if (type == StoreProductType.Unknown) untyped = id;
            }
            string transaction = order.Info?.TransactionID;
            // A confirm of a product Unity IAP could not type acknowledges it instead of consuming it on Google Play: a consumable
            // would be closed ungranted and never consumed (issue #329)
            if (untyped != null)
            {
                if (_keptUnrequested.Add(transaction ?? ""))
                    Warn($"A paid purchase of {untyped} (transaction {transaction}) names a product this session's catalog does not list and the store did not type: it is not confirmed, and the store lists it again at the next purchase fetch (each start and Restore). Google Play refunds a purchase not acknowledged within three days; list the product in the catalog to grant it.");
                return false;
            }
            if (consumables.Count == 0) return true;   // an owned product: Changed reports it
            var handler = UnrequestedPurchase;
            if (handler == null)
            {
                // once per order and session: on Google Play and the App Store a fetch hands the order to OnPending and OnFetched
                if (!_keptUnrequested.Add(transaction ?? "")) return false;
                Warn($"A paid purchase of {string.Join(", ", consumables)} (transaction {transaction}) was not requested in this session and nothing handles UnrequestedPurchase: it is not confirmed, and the store lists it again at the next purchase fetch (each start and Restore). Subscribe IapService.UnrequestedPurchase before SetStoreAdapter.");
                return false;
            }
            try
            {
                foreach (var id in consumables) handler(id, transaction);
                return true;
            }
            catch (Exception e)
            {
                Warn($"UnrequestedPurchase for {string.Join(", ", consumables)} (transaction {transaction}) threw {e.GetType().Name}: {e.Message}. The order is not confirmed; the next purchase fetch reports it again.");
                return false;
            }
        }

        private bool CanFinishConsumables(PendingOrder order)
        {
            if (UnityIAPServices.GetDefaultStore() != GooglePlay.Name) return true;
            foreach (var item in order.CartOrdered.Items())
            {
                if (TypeOf(item) != StoreProductType.Consumable) continue;
                // Google consumes by the selected listing; the Nexus catalog cannot fix its type (#332).
                var definition = item.Product.catalogListings.TryGetValue(item.CatalogListingId, out var listing) ? listing.definition : null;
                if (definition?.type == StoreProductType.Consumable) continue;
                string transaction = order.Info?.TransactionID;
                if (_keptUnsafeCarts.Add(transaction ?? ""))
                    Warn($"A paid consumable purchase of {item.Product.definition.id} (transaction {transaction}) has a cart that Google Play cannot finish as consumable: it is not granted or confirmed, and the store lists it again at the next purchase fetch (each start and Restore). Fetch a consumable store definition before retrying; Google Play refunds a purchase not acknowledged within three days.");
                return false;
            }
            return true;
        }

        /// <summary>Starts a new round of confirms for an order: a purchase, or a fetch that lists it again.</summary>
        private void Track(PendingOrder order)
        {
            string id = order?.Info?.TransactionID;
            if (string.IsNullOrEmpty(id)) return;   // Unity IAP refuses its confirm; OnConfirmed warns
            _unconfirmed[id] = order;
            _confirmAttempts[id] = 0;
        }

        private void Confirm(PendingOrder order)
        {
            if (!CanFinishConsumables(order)) return;
            string id = order?.Info?.TransactionID;
            if (!string.IsNullOrEmpty(id) && _confirmAttempts.TryGetValue(id, out int sent)) _confirmAttempts[id] = sent + 1;
            _store.ConfirmPurchase(order);
        }

        private void OnConfirmed(Order order)
        {
            string id = order?.Info?.TransactionID;
            if (order is FailedOrder failed)
            {
                // the same order is already being confirmed (a fetch and a retry crossed); that confirm's answer follows
                if (failed.FailureReason == PurchaseFailureReason.ExistingPurchasePending) return;
                string reason = $"{failed.FailureReason}: {failed.Details}";
                if (string.IsNullOrEmpty(id) || !_unconfirmed.TryGetValue(id, out var pending))
                {
                    Warn($"The store did not confirm an order of {Products(failed)} ({reason}).");
                    return;
                }
                int attempt = _confirmAttempts[id], total = ConfirmRetryDelays.Length + 1;
                if (attempt < total)
                {
                    var delay = ConfirmRetryDelays[attempt - 1];
                    Warn($"The store did not confirm the purchase of {Products(pending)} ({reason}), attempt {attempt} of {total}; it is sent again in {delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.");
                    RetryConfirm(id, delay);
                }
                else
                {
                    Warn($"The store did not confirm the purchase of {Products(pending)} after {total} attempts ({reason}). The player has it; the next purchase fetch (each start and Restore) confirms it again; Google Play refunds a purchase not acknowledged within three days.");
                }
                return;
            }
            if (!string.IsNullOrEmpty(id) && _unconfirmed.Remove(id))
            {
                _confirmAttempts.Remove(id);
                Changed?.Invoke();
            }
        }

        private async void RetryConfirm(string id, TimeSpan delay) // NEXUS002-exempt: started from a store callback that must return first; the whole body is caught
        {
            try
            {
                // Unity IAP drops the refused request only after this callback returns: a confirm sent inside it is refused as a duplicate
                await Task.Yield();
                await Task.Delay(delay);
                if (_disposed || !_unconfirmed.TryGetValue(id, out var order)) return;   // disposed, or confirmed meanwhile
                if (!_connected)
                {
                    Warn($"The purchase of {Products(order)} is not confirmed: the store is disconnected. The next purchase fetch (each start and Restore) confirms it.");
                    return;
                }
                Confirm(order);
            }
            catch (Exception e)
            {
                Warn($"The confirm retry of transaction {id} failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static string Products(Order order)
        {
            var ids = new List<string>();
            foreach (var item in order.CartOrdered.Items()) ids.Add(item.Product?.definition?.id);
            return string.Join(", ", ids);
        }

        private void OnFailed(FailedOrder failed)
        {
            Warn($"Purchase failed: {failed.FailureReason} {failed.Details}");
            if (!Answers(failed)) return;   // another product's payment failed (a deferred one, for example): the waiting request goes on
            Complete(failed.FailureReason == PurchaseFailureReason.UserCancelled ? PurchaseOutcome.Cancelled
                : failed.FailureReason == PurchaseFailureReason.ProductUnavailable || failed.FailureReason == PurchaseFailureReason.PurchasingUnavailable || failed.FailureReason == PurchaseFailureReason.StoreNotConnected ? PurchaseOutcome.Unavailable
                : PurchaseOutcome.Failed);
        }

        /// <summary>Consumables are never owned; non-consumables and subscriptions are owned once ordered. A pending order of a
        /// product neither the catalog nor the store typed is not owned: it is not confirmed (GrantUnrequested), and Google Play
        /// refunds it (issue #329).</summary>
        private void Own(Order order)
        {
            Defer(order, false);
            foreach (var item in order.CartOrdered.Items())
            {
                string id = item.Product?.definition?.id;
                if (string.IsNullOrEmpty(id)) continue;
                var type = TypeOf(item);
                if (type == StoreProductType.Consumable || (type == StoreProductType.Unknown && order is PendingOrder)) continue;
                _owned.Add(id);
            }
        }

        // The catalog's type when it lists the product; otherwise the store's, so a consumable removed from the catalog while a
        // purchase of it was pending is not owned and not consumed ungranted (issue #329). Unity IAP types a product it could
        // not resolve Unknown (Product.CreateUnknownProduct).
        private StoreProductType TypeOf(CartItem item)
        {
            string id = item.Product?.definition?.id;
            if (id != null && _catalog.TryGetValue(id, out var type)) return Map(type);
            return item.Product?.definition?.type ?? StoreProductType.Unknown;
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

        /// <summary>True when the order answers the waiting request: it names the requested product, or it names none (a failure
        /// Unity IAP could not tie to a product), which would otherwise leave the game's callback unanswered.</summary>
        private bool Answers(Order order)
        {
            if (_pending == null) return false;
            bool named = false;
            var cart = order?.CartOrdered;
            if (cart != null)
                foreach (var item in cart.Items())
                {
                    string id = item.Product?.definition?.id;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id == _pendingProductId) return true;
                    named = true;
                }
            return !named;
        }

        private void Complete(PurchaseOutcome outcome) { var done = _pending; _pending = null; _pendingProductId = null; done?.Invoke(outcome); }
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
