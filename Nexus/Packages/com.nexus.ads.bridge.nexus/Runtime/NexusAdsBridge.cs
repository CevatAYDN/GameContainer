using System;
using Nexus.Ads.Unity;
using Nexus.Core.Services;

namespace Nexus.Ads.Bridge
{
    /// <summary>
    /// The one call a Nexus game makes: registers the adapter with the factory under
    /// <see cref="NexusAdsAdapter.ProviderName"/> and the running provider's name, hands it to the
    /// service, sets the service's interstitial cooldown to the policy floor, forwards revenue to
    /// <see cref="AdService.RaiseImpression"/>, and wires the ad-free entitlement when a store and a
    /// product are given. Waits for the bootstrap when it has not started yet.
    /// </summary>
    public static class NexusAdsBridge
    {
        private static readonly System.Collections.Generic.HashSet<AdController> Forwarded = new System.Collections.Generic.HashSet<AdController>();

        /// <summary>
        /// Connects <paramref name="ads"/> to the controller of <see cref="AdsBootstrap.Instance"/> (or the one given).
        /// Safe to call before the bootstrap finished starting; the connection completes on its Started event.
        /// </summary>
        public static void Connect(IAdService ads, IAdAdapterFactory factory, IIapService store = null, string removeAdsProductId = null, AdsBootstrap bootstrap = null)
        {
            if (ads == null) throw new ArgumentNullException(nameof(ads));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            bootstrap = bootstrap ?? AdsBootstrap.Instance;
            if (bootstrap == null) throw new InvalidOperationException("No AdsBootstrap in the scene; add one (it loads Resources/" + AdsSettings.ResourceName + ").");
            if (bootstrap.Controller != null) { Attach(ads, factory, store, removeAdsProductId, bootstrap); return; }
            Action once = null;
            once = () => { bootstrap.Started -= once; Attach(ads, factory, store, removeAdsProductId, bootstrap); };
            bootstrap.Started += once;
        }

        /// <summary>Connects a controller directly (tests, custom hosts).</summary>
        public static NexusAdsAdapter Attach(IAdService ads, IAdAdapterFactory factory, AdController controller, string providerName, IIapService store = null, string removeAdsProductId = null)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            var adapter = new NexusAdsAdapter(controller);
            factory.RegisterProvider(NexusAdsAdapter.ProviderName, () => adapter);
            if (!string.IsNullOrEmpty(providerName)) factory.RegisterProvider(providerName, () => adapter);
            ads.SetInterstitialCooldown((float)controller.Guard.Policy.MinInterstitialGapSeconds);
            if (ads is AdService service && Forwarded.Add(controller)) controller.Revenue += r => service.RaiseImpression(r.Network, r.Value, r.Placement); // one forward per controller, however often Connect runs
            if (store != null && !string.IsNullOrEmpty(removeAdsProductId)) controller.SetAdFreeEntitlement(new IapAdFreeEntitlement(store, removeAdsProductId));
            ads.SetNetworkAdapter(factory.CreateAdapter(NexusAdsAdapter.ProviderName));
            return adapter;
        }

        private static void Attach(IAdService ads, IAdAdapterFactory factory, IIapService store, string removeAdsProductId, AdsBootstrap bootstrap)
            => Attach(ads, factory, bootstrap.Controller, bootstrap.Provider?.Name, store, removeAdsProductId);
    }
}
