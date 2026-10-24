using System.Threading;
using System.Threading.Tasks;
using Nexus.Ads.Bridge;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusAdsSamples.NexusGame
{
    /// <summary>
    /// A Nexus game's ad wiring: the services from com.nexus.core, the bridge from
    /// com.nexus.ads.bridge.nexus, an AdsBootstrap (com.nexus.ads) in the scene.
    /// Game code talks to <see cref="IAdService"/> only, as it did before the ad package family existed.
    /// Put this lifecycle on the Root's GameObject (beside the bootstrap) and press Play: the editor's
    /// simulator pays. Root registers every IContextLifecycle component on its GameObject and exposes
    /// the context as Root.Context, which is how this component reaches the services.
    /// </summary>
    [RequireComponent(typeof(Root))]
    public sealed class NexusAdsSample : MonoBehaviour, IContextLifecycle
    {
        public const string RewardedPlacement = "shop_refill";

        private Root _root;
        private int _coins;
        private string _last = "";

        private void Awake() => _root = GetComponent<Root>();

        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindService<IAdService, AdService>();
            builder.BindInstance<IAdAdapterFactory>(new AdAdapterFactory());
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;

        public ValueTask OnStartAsync(CancellationToken ct)
        {
            var context = _root.Context;
            NexusAdsBridge.Connect(context.Resolve<IAdService>(), context.Resolve<IAdAdapterFactory>());
            return default;
        }

        public void OnDispose() { }

        private void OnGUI()
        {
            var ads = _root != null && _root.Context != null ? _root.Context.TryResolve<IAdService>() : null;
            if (ads == null) return;
            GUILayout.BeginArea(new Rect(16, 16, 320, 160));
            GUILayout.Label("Coins: " + _coins);
            GUI.enabled = ads.IsRewardedAvailable(RewardedPlacement);
            if (GUILayout.Button(GUI.enabled ? "Watch an ad: +50 coins" : "Rewarded ad loading..."))
                ads.RequestReward(RewardedPlacement, outcome => { _last = outcome.ToString(); if (outcome == AdOutcome.Rewarded) _coins += 50; });
            GUI.enabled = true;
            GUILayout.Label(_last);
            GUILayout.EndArea();
        }
    }
}
