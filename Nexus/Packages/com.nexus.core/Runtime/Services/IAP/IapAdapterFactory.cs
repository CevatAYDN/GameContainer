using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Factory for creating IAP store adapters.
    /// Allows runtime selection of store provider (Unity IAP, RevenueCat, Google Play Billing, etc.)
    /// without changing the consuming code.
    /// </summary>
    [Preserve]
    public interface IIapAdapterFactory
    {
        /// <summary>Creates a store adapter for the specified provider.</summary>
        /// <param name="provider">Provider identifier: "unityiap", "revenuecat", "googleplay", "apple", "mock"</param>
        /// <returns>Configured adapter instance, or null if provider is unknown.</returns>
        IIapStoreAdapter CreateAdapter(string provider);
        
        /// <summary>Registers a custom adapter creator for a provider.</summary>
        void RegisterProvider(string provider, Func<IIapStoreAdapter> creator);
    }

    /// <summary>
    /// Default implementation of <see cref="IIapAdapterFactory"/>.
    /// Comes with built-in mock provider; real providers are registered by the consumer project.
    /// </summary>
    [Preserve]
    public sealed class IapAdapterFactory : ProviderFactory<IIapStoreAdapter>, IIapAdapterFactory
    {
        public IapAdapterFactory() : base("IAP provider")
        {
            RegisterBuiltIn("mock", () => new MockIapStoreAdapter());
        }
    }

    /// <summary>
    /// Mock IAP store adapter for development and testing.
    /// Simulates purchase flow, restore, and product ownership without real store SDK.
    /// </summary>
    [Preserve]
    public sealed class MockIapStoreAdapter : IIapStoreAdapter
    {
        private readonly HashSet<string> _ownedProducts = new();
        private bool _initialized;
        private List<ProductDefinition> _catalog = new();

        public void Initialize(List<ProductDefinition> products, Action<bool> onInitialized)
        {
            _catalog = products ?? new List<ProductDefinition>();
            _initialized = true;
            onInitialized?.Invoke(true);
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

            // Mock: always succeed in editor/dev, configurable in release
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _ownedProducts.Add(productId);
            onComplete?.Invoke(true, productId);
#else
            // Release mock behavior: graceful failure
            onComplete?.Invoke(false, "store_unavailable");
#endif
        }

        public void Restore(Action<bool> onComplete)
        {
            if (!_initialized)
            {
                onComplete?.Invoke(false);
                return;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            onComplete?.Invoke(true);
#else
            onComplete?.Invoke(false);
#endif
        }

        public bool IsOwned(string productId)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return _ownedProducts.Contains(productId);
#else
            return false;
#endif
        }

        /// <summary>Manually set product ownership for testing scenarios.</summary>
        public void SetOwned(string productId, bool owned)
        {
            if (owned) _ownedProducts.Add(productId);
            else _ownedProducts.Remove(productId);
        }
    }
}