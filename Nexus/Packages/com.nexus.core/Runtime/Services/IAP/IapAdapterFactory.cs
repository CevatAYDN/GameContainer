using System;
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
        /// <param name="provider">The name a provider was registered under with <see cref="RegisterProvider"/>.</param>
        /// <returns>Configured adapter instance, or null if provider is unknown.</returns>
        IIapStoreAdapter CreateAdapter(string provider);
        
        /// <summary>Registers a custom adapter creator for a provider.</summary>
        void RegisterProvider(string provider, Func<IIapStoreAdapter> creator);
    }

    /// <summary>
    /// Default implementation of <see cref="IIapAdapterFactory"/>. It ships no provider: the game registers the real ones
    /// (<c>UnityIapAdapter.Register</c> in com.nexus.iap.unity). The test double <c>MockIapStoreAdapter</c> lives in the
    /// testing kit (#162).
    /// </summary>
    [Preserve]
    public sealed class IapAdapterFactory : ProviderFactory<IIapStoreAdapter>, IIapAdapterFactory
    {
        public IapAdapterFactory() : base("IAP provider") { }
    }
}
