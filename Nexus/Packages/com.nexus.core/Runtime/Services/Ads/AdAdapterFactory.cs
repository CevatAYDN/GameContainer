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
        /// <param name="provider">Provider identifier: "admob", "applovin", "ironsource", "unityads", "mock"</param>
        /// <returns>Configured adapter instance, or null if provider is unknown.</returns>
        IAdNetworkAdapter CreateAdapter(string provider);
        
        /// <summary>Registers a custom adapter creator for a provider.</summary>
        void RegisterProvider(string provider, Func<IAdNetworkAdapter> creator);
    }

    /// <summary>
    /// Default implementation of <see cref="IAdAdapterFactory"/>.
    /// Comes with built-in mock provider; real providers are registered by the consumer project.
    /// </summary>
    [Preserve]
    public sealed class AdAdapterFactory : ProviderFactory<IAdNetworkAdapter>, IAdAdapterFactory
    {
        public AdAdapterFactory() : base("ad provider")
        {
            // Built-in mock provider for development/testing
            RegisterBuiltIn("mock", () => new MockAdNetworkAdapter());
        }
    }

    /// <summary>
    /// Mock ad network adapter for development and testing.
    /// Simulates ad loading, showing, and callbacks without real SDK.
    /// </summary>
    [Preserve]
    public sealed class MockAdNetworkAdapter : IAdNetworkAdapter
    {
        private readonly System.Collections.Generic.Dictionary<string, bool> _interstitialReady = new();
        private readonly System.Collections.Generic.Dictionary<string, bool> _rewardedReady = new();
        private bool _initialized;

        public void Initialize(Action onInitialized)
        {
            _initialized = true;
            // Mock: everything is ready immediately
            _interstitialReady["default"] = true;
            _rewardedReady["default"] = true;
            onInitialized?.Invoke();
        }

        public bool IsInterstitialReady(string placement)
        {
            return _initialized && _interstitialReady.TryGetValue(placement, out var ready) && ready;
        }

        public void ShowInterstitial(string placement, Action onClosed)
        {
            if (!IsInterstitialReady(placement))
            {
                onClosed?.Invoke();
                return;
            }
            // Mock stays ready: AdService's cooldown gates pacing. Consuming here would
            // make the demo's interstitial a one-shot for the whole session.
            NexusRuntime.Logger?.Log($"[MockAdAdapter] Showing interstitial: {placement}");
            onClosed?.Invoke();
        }

        public bool IsRewardedReady(string placement)
        {
            return _initialized && _rewardedReady.TryGetValue(placement, out var ready) && ready;
        }

        public void ShowRewarded(string placement, Action<bool> onCompleted)
        {
            if (!IsRewardedReady(placement))
            {
                onCompleted?.Invoke(false);
                return;
            }
            // Mock stays ready (see ShowInterstitial): the AdService cooldown paces shows.
            NexusRuntime.Logger?.Log($"[MockAdAdapter] Showing rewarded: {placement}");
            onCompleted?.Invoke(true);
        }

        public void ShowBanner(string placement, string position)
        {
            NexusRuntime.Logger?.Log($"[MockAdAdapter] Showing banner: {placement} at {position}");
        }

        public void HideBanner()
        {
            NexusRuntime.Logger?.Log($"[MockAdAdapter] Hiding banner");
        }

        /// <summary>Manually set ad readiness for testing scenarios.</summary>
        public void SetInterstitialReady(string placement, bool ready) => _interstitialReady[placement] = ready;
        public void SetRewardedReady(string placement, bool ready) => _rewardedReady[placement] = ready;
    }
}