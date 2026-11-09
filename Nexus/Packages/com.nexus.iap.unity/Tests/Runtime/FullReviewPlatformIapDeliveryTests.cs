#if UNITY_INCLUDE_TESTS && NEXUS_UNITY_IAP && NEXUS_FULL_REVIEW_IAP_550
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nexus.Core.Services;
using Nexus.Iap.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;
using UnityEngine.TestTools;
using NexusProduct = Nexus.Core.Services.ProductDefinition;
using NexusProductType = Nexus.Core.Services.ProductType;
using StoreProduct = UnityEngine.Purchasing.ProductDefinition;
using StoreProductType = UnityEngine.Purchasing.ProductType;

namespace Nexus.Editor.Tests
{
    // Enable only in the registered IAP 5.5.0 consumer. All three SDK services are in-memory replacements.
    public sealed class FullReviewPlatformIapDeliveryTests
    {
        [Test]
        public void P01_FailedRequestedGrant_IsRedeliveredBeforeAnyConfirmation()
        {
            string previousStore = UnityIAPServices.GetDefaultStore();
            var delivered = new HashSet<string>();
            var sdk = new ControlledServices(() => delivered.Contains(ControlledServices.TransactionId));
            var adapter = new UnityIapAdapter();
            int requestedCallbacks = 0;
            int recoveredCallbacks = 0;
            try
            {
                UseControlledServices(sdk);
                adapter.UnrequestedPurchase += (product, transaction) =>
                {
                    Assert.That(product, Is.EqualTo(ControlledServices.ProductId));
                    recoveredCallbacks++;
                    delivered.Add(transaction);
                };
                Initialize(adapter);

                var deliveryFailure = new DeliveryNotSavedException();
                Assert.That(Assert.Throws<DeliveryNotSavedException>(() =>
                    adapter.PurchaseWithOutcome(ControlledServices.ProductId, outcome =>
                    {
                        Assert.That(outcome, Is.EqualTo(PurchaseOutcome.Purchased));
                        Assert.That(adapter.IsConfirmationPending(ControlledServices.ProductId), Is.True);
                        requestedCallbacks++;
                        throw deliveryFailure;
                    })), Is.SameAs(deliveryFailure));
                Assert.That(requestedCallbacks, Is.EqualTo(1));
                Assert.That(sdk.ConfirmationCalls, Is.Zero, "A failed grant must not be confirmed.");
                Assert.That(adapter.IsConfirmationPending(ControlledServices.ProductId), Is.False,
                    "A failed delivery must not remain recorded as granted.");

                bool? restored = null;
                adapter.Restore(ok => restored = ok);

                Assert.That(sdk.ConfirmedWithoutDelivery, Is.False,
                    "A later fetch must not confirm a transaction whose requested grant failed.");
                Assert.That(recoveredCallbacks, Is.EqualTo(1), "Recovery must redeliver the ungranted transaction.");
                Assert.That(delivered.Contains(ControlledServices.TransactionId), Is.True);
                Assert.That(sdk.ConfirmationCalls, Is.EqualTo(1));
                Assert.That(restored, Is.True);
            }
            finally
            {
                try { adapter.Dispose(); }
                finally { UnityIAPServices.SetStoreAsDefault(previousStore); }
            }
        }

        [Test]
        public void P01_DeliveredConsumable_IsNotGrantedAgainWhenConfirmationRetries()
        {
            string previousStore = UnityIAPServices.GetDefaultStore();
            bool delivered = false;
            var sdk = new ControlledServices(() => delivered) { ConfirmationFailuresRemaining = 1 };
            var adapter = new UnityIapAdapter { ConfirmRetryDelays = Array.Empty<TimeSpan>() };
            int requestedCallbacks = 0;
            int recoveredCallbacks = 0;
            try
            {
                UseControlledServices(sdk);
                adapter.UnrequestedPurchase += (_, _) => recoveredCallbacks++;
                Initialize(adapter);

                LogAssert.Expect(LogType.Warning, new Regex(
                    @"^\[UnityIAP\] The store did not confirm the purchase of full_review_coins after 1 attempts"));
                adapter.PurchaseWithOutcome(ControlledServices.ProductId, outcome =>
                {
                    Assert.That(outcome, Is.EqualTo(PurchaseOutcome.Purchased));
                    Assert.That(adapter.IsConfirmationPending(ControlledServices.ProductId), Is.True);
                    requestedCallbacks++;
                    delivered = true;
                });
                Assert.That(sdk.ConfirmationCalls, Is.EqualTo(1));
                Assert.That(adapter.IsConfirmationPending(ControlledServices.ProductId), Is.True,
                    "A refused confirmation must retain the successful delivery for the next fetch.");

                bool? restored = null;
                adapter.Restore(ok => restored = ok);

                Assert.That(restored, Is.True);
                Assert.That(requestedCallbacks, Is.EqualTo(1));
                Assert.That(recoveredCallbacks, Is.Zero, "Retry confirmation, not the successful consumable grant.");
                Assert.That(sdk.ConfirmationCalls, Is.EqualTo(2));
                Assert.That(sdk.ConfirmedWithoutDelivery, Is.False);
                Assert.That(adapter.IsConfirmationPending(ControlledServices.ProductId), Is.False);
                Assert.That(adapter.IsOwned(ControlledServices.ProductId), Is.False, "Consumables are never owned.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                try { adapter.Dispose(); }
                finally { UnityIAPServices.SetStoreAsDefault(previousStore); }
            }
        }

        private static void UseControlledServices(ControlledServices sdk)
        {
            string storeName = "NexusFullReviewP01_" + Guid.NewGuid().ToString("N");
            UnityIAPServices.AddNewCustomStore(new GuardStoreWrapper(storeName));
            UnityIAPServices.AddNewStoreService(storeName, () => sdk);
            UnityIAPServices.AddNewProductService(storeName, () => sdk);
            UnityIAPServices.AddNewPurchaseService(storeName, () => sdk);
            UnityIAPServices.SetStoreAsDefault(storeName);
            Assert.That(UnityIAPServices.DefaultStore(), Is.SameAs(sdk));
            Assert.That(UnityIAPServices.DefaultProduct(), Is.SameAs(sdk));
            Assert.That(UnityIAPServices.DefaultPurchase(), Is.SameAs(sdk));
        }

        private static void Initialize(UnityIapAdapter adapter)
        {
            bool? initialized = null;
            adapter.Initialize(new List<NexusProduct>
            {
                new NexusProduct { Id = ControlledServices.ProductId, Type = NexusProductType.Consumable }
            }, ready => initialized = ready);
            Assert.That(initialized, Is.True, "The controlled SDK completes initialization synchronously.");
        }

        private sealed class DeliveryNotSavedException : Exception { }

        private sealed class ControlledServices : IStoreService, IProductService, IPurchaseService
        {
            public const string ProductId = "full_review_coins";
            public const string TransactionId = "full_review_transaction";
            private readonly Func<bool> _deliverySaved;
            private readonly ObservableCollection<Product> _products = new ObservableCollection<Product>();
            private readonly ObservableCollection<Order> _orders = new ObservableCollection<Order>();
            private PendingOrder _pending;
            private ConnectionState _connection = ConnectionState.Disconnected;

            public ControlledServices(Func<bool> deliverySaved)
            {
                _deliverySaved = deliverySaved;
                // Product's SDK constructor is internal; no Nexus implementation state is changed by this fixture.
                _products.Add((Product)Activator.CreateInstance(typeof(Product),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { new StoreProduct(ProductId, StoreProductType.Consumable), new ProductMetadata(), true }, null));
            }

            public int ConfirmationCalls { get; private set; }
            public bool ConfirmedWithoutDelivery { get; private set; }
            public int ConfirmationFailuresRemaining;
            public event Action OnStoreConnected;
            public event Action<List<Product>> OnProductsFetched;
            public event Action<PendingOrder> OnPurchasePending;
            public event Action<Orders> OnPurchasesFetched;
            public event Action<Order> OnPurchaseConfirmed;
            public event Action<StoreConnectionFailureDescription> OnStoreDisconnected { add { } remove { } }
            public event Action OnAuthAccountChanged { add { } remove { } }
            public event Action<ProductFetchFailed> OnProductsFetchFailed { add { } remove { } }
            public event Action<FailedOrder> OnPurchaseFailed { add { } remove { } }
            public event Action<DeferredOrder> OnPurchaseDeferred { add { } remove { } }
            public event Action<PurchasesFetchFailureDescription> OnPurchasesFetchFailed { add { } remove { } }
            public event Action<Entitlement> OnCheckEntitlement { add { } remove { } }

            IAppleStoreExtendedService IStoreService.Apple => null;
            IGooglePlayStoreExtendedService IStoreService.Google => null;
            IPaymentProvidersExtendedService IStoreService.PaymentProviders => null;
            IAppleStoreExtendedProductService IProductService.Apple => null;
            IAppleStoreExtendedPurchaseService IPurchaseService.Apple => null;
            IGooglePlayStoreExtendedPurchaseService IPurchaseService.Google => null;
            IPaymentProvidersExtendedPurchaseService IPurchaseService.PaymentProviders => null;

            public Task Connect()
            {
                _connection = ConnectionState.Connected;
                OnStoreConnected?.Invoke();
                return Task.CompletedTask;
            }

            public ConnectionState GetConnectionState() => _connection;
            public void SetStoreReconnectionRetryPolicyOnDisconnection(IRetryPolicy retryPolicy) { }
            public Product GetProductById(string id) => id == ProductId ? _products[0] : null;
            public Product GetProductByCatalogListingId(string id) => GetProductById(id);
            public ReadOnlyObservableCollection<Product> GetProducts() => new ReadOnlyObservableCollection<Product>(_products);
            public ReadOnlyObservableCollection<Order> GetPurchases() => new ReadOnlyObservableCollection<Order>(_orders);
            public void FetchProductsWithNoRetries(List<StoreProduct> products) => FetchProducts(products);
            public void FetchProducts(List<StoreProduct> products, IRetryPolicy retryPolicy = null) =>
                OnProductsFetched?.Invoke(new List<Product>(_products));

            public void PurchaseProduct(string id) => PurchaseProduct(GetProductById(id));
            public void PurchaseProduct(Product product) => Purchase(new Cart(new CartItem(product)));

            public void Purchase(ICart cart)
            {
                _pending = new PendingOrder(cart, new FixtureOrderInfo());
                _orders.Add(_pending);
                OnPurchasePending?.Invoke(_pending);
            }

            public void FetchPurchases()
            {
                var pending = new List<PendingOrder>();
                if (_pending != null) pending.Add(_pending);
                OnPurchasesFetched?.Invoke(new Orders(new List<ConfirmedOrder>(), pending, new List<DeferredOrder>()));
            }

            public void RestoreTransactions(Action<bool, string> callback)
            {
                FetchPurchases();
                callback?.Invoke(true, null);
            }

            public void ConfirmPurchase(PendingOrder order)
            {
                ConfirmationCalls++;
                ConfirmedWithoutDelivery |= !_deliverySaved();
                if (ConfirmationFailuresRemaining > 0)
                {
                    ConfirmationFailuresRemaining--;
                    OnPurchaseConfirmed?.Invoke(new FailedOrder(order, PurchaseFailureReason.Unknown,
                        "controlled confirmation failure"));
                    return;
                }
                _pending = null;
                _orders.Remove(order);
                OnPurchaseConfirmed?.Invoke(new ConfirmedOrder(order.CartOrdered, order.Info));
            }

            public void CheckEntitlement(Product product) => throw new NotSupportedException();
            public void ProcessPendingOrdersOnPurchasesFetched(bool shouldProcess) => throw new NotSupportedException();
        }

        private sealed class FixtureOrderInfo : IOrderInfo
        {
            public IAppleOrderInfo Apple => null;
            public IGoogleOrderInfo Google => null;
            public IPaymentProvidersOrderInfo PaymentProviders => null;
            public List<IPurchasedProductInfo> PurchasedProductInfo { get; set; } = new List<IPurchasedProductInfo>();
            public string Receipt => "fixture_receipt";
            public string TransactionID => ControlledServices.TransactionId;
        }

        private sealed class GuardStoreWrapper : IStoreWrapper
        {
            public GuardStoreWrapper(string storeName) => name = storeName;
            public string name { get; }
            public Store instance { get; } = new GuardStore();
            public ConnectionState GetStoreConnectionState() => ConnectionState.Connected;
        }

        private sealed class GuardStore : Store
        {
            public override void Connect() => throw Unexpected();
            public override void FetchProducts(IReadOnlyCollection<StoreProduct> products) => throw Unexpected();
            public override void FetchPurchases() => throw Unexpected();
            public override void Purchase(ICart cart) => throw Unexpected();
            public override void FinishTransaction(PendingOrder order) => throw Unexpected();
            public override void CheckEntitlement(StoreProduct product) => throw Unexpected();
            private static Exception Unexpected() => new InvalidOperationException("The controlled SDK services must handle this call.");
        }
    }
}
#endif
