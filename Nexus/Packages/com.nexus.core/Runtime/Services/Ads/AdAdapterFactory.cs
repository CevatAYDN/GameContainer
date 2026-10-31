using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Factory for creating ad network adapters.
    /// Allows runtime selection of ad provider (AdMob, AppLovin MAX, IronSource LevelPlay, etc.)
    /// without changing the consuming code.
    /// </summary>
    [Preserve]
    public interface IAdAdapterFactory
    {
        /// <summary>Creates an ad network adapter for the specified provider.</summary>
        /// <param name="provider">The name a provider was registered under with <see cref="RegisterProvider"/>.</param>
        /// <returns>Configured adapter instance, or null if provider is unknown.</returns>
        IAdNetworkAdapter CreateAdapter(string provider);
        
        /// <summary>Registers a custom adapter creator for a provider.</summary>
        void RegisterProvider(string provider, Func<IAdNetworkAdapter> creator);
    }

    /// <summary>
    /// Default implementation of <see cref="IAdAdapterFactory"/>. It ships no provider: the game registers the real ones
    /// (the ad family's bridge, an SDK adapter). The test double <c>MockAdNetworkAdapter</c> lives in the testing kit (#162).
    /// </summary>
    [Preserve]
    public sealed class AdAdapterFactory : ProviderFactory<IAdNetworkAdapter>, IAdAdapterFactory
    {
        public AdAdapterFactory() : base("ad provider") { }
    }
}
