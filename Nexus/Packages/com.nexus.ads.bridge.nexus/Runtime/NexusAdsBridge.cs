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
        // What one Attach wired to a controller. The next Attach for the same controller (a game that rebuilt its context) detaches it:
        // revenue went to the first service for good, and each Attach left an adapter and an entitlement subscribed (issues #118, #177).
        private sealed class Connection
        {
            public NexusAdsAdapter Adapter;
            public Action<AdRevenue> Revenue;
            public IapAdFreeEntitlement Entitlement;
        }

        private static readonly System.Collections.Generic.Dictionary<AdController, Connection> Connections = new System.Collections.Generic.Dictionary<AdController, Connection>();

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
            Connections.TryGetValue(controller, out var previous);
            if (previous != null)
            {
                if (previous.Revenue != null) controller.Revenue -= previous.Revenue;
                previous.Entitlement?.Dispose();
                previous.Adapter.Dispose();
            }
            var adapter = new NexusAdsAdapter(controller);
            var connection = new Connection { Adapter = adapter };
            Connections[controller] = connection;
            factory.RegisterProvider(NexusAdsAdapter.ProviderName, () => adapter);
            if (!string.IsNullOrEmpty(providerName)) factory.RegisterProvider(providerName, () => adapter);
            ads.SetInterstitialCooldown((float)controller.Guard.Policy.MinInterstitialGapSeconds);
            if (ads is AdService service)
            {
                connection.Revenue = r => service.RaiseImpression(r.Network, r.Value, r.Placement);
                controller.Revenue += connection.Revenue;
            }
            if (store != null && !string.IsNullOrEmpty(removeAdsProductId))
            {
                connection.Entitlement = new IapAdFreeEntitlement(store, removeAdsProductId);
                controller.SetAdFreeEntitlement(connection.Entitlement);
            }
            else if (previous?.Entitlement != null) controller.SetAdFreeEntitlement(null); // the disposed entitlement must not keep answering
            ads.SetNetworkAdapter(factory.CreateAdapter(NexusAdsAdapter.ProviderName));
            return adapter;
        }

        private static void Attach(IAdService ads, IAdAdapterFactory factory, IIapService store, string removeAdsProductId, AdsBootstrap bootstrap)
            => Attach(ads, factory, bootstrap.Controller, bootstrap.Provider?.Name, store, removeAdsProductId);
    }
}
