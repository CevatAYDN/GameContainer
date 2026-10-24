using System;
using Nexus.Core.Services;

namespace Nexus.Ads.Bridge
{
    /// <summary>
    /// The <see cref="AdController"/> seen through <c>com.nexus.core</c>'s adapter contract: the base
    /// <see cref="IAdNetworkAdapter"/> plus the outcome, readiness and consent capabilities, so
    /// <see cref="AdService"/> and every game written against <see cref="IAdService"/> run unchanged.
    /// Placements are the controller's; the guard decides, the service's own cooldown sits at the
    /// policy's interstitial floor (set by <see cref="NexusAdsBridge"/>). Revenue is forwarded to
    /// <see cref="AdService.RaiseImpression"/> by the bridge.
    /// </summary>
    public sealed class NexusAdsAdapter : IAdNetworkAdapter, IAdOutcomeAdapter, IAdReadinessAdapter, IAdConsentAdapter
    {
        public const string ProviderName = "nexus-ads";

        private readonly AdController _controller;
        private Action _onInitialized;

        public event Action ReadyChanged;

        public AdController Controller => _controller;

        public NexusAdsAdapter(AdController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _controller.ReadyChanged += OnReadyChanged;
        }

        /// <summary>Starts the controller when it is not running yet; <paramref name="onInitialized"/> runs once, when the first readiness answer exists.</summary>
        public void Initialize(Action onInitialized)
        {
            _onInitialized = onInitialized;
            if (_controller.State == AdController.Phase.Stopped) _controller.Start();
            if (_controller.State != AdController.Phase.AwaitingConsent && _controller.State != AdController.Phase.Initializing) CompleteInitialize();
        }

        private void OnReadyChanged()
        {
            if (_onInitialized != null && _controller.State != AdController.Phase.AwaitingConsent && _controller.State != AdController.Phase.Initializing) CompleteInitialize();
            ReadyChanged?.Invoke();
        }

        private void CompleteInitialize()
        {
            var callback = _onInitialized;
            _onInitialized = null;
            callback?.Invoke();
        }

        public bool IsInterstitialReady(string placement) => _controller.IsReady(placement);

        public void ShowInterstitial(string placement, Action onClosed) => _controller.Show(placement, _ => onClosed?.Invoke());

        public bool IsRewardedReady(string placement) => _controller.IsReady(placement);

        public void ShowRewarded(string placement, Action<bool> onCompleted) => _controller.Show(placement, result => onCompleted?.Invoke(result == AdResult.Rewarded));

        public void RequestReward(string placement, Action<AdOutcome> onOutcome) => _controller.Show(placement, result => onOutcome?.Invoke(Map(result)));

        public void ShowBanner(string placement, string position)
            => _controller.ShowBanner(placement, string.Equals(position, "top", StringComparison.OrdinalIgnoreCase) ? BannerPosition.Top : BannerPosition.Bottom);

        public void HideBanner() => _controller.HideBanner();

        public bool PrivacyOptionsRequired => _controller.PrivacyOptionsRequired;

        public void ShowPrivacyOptions(Action onClosed) => _controller.ShowPrivacyOptions(onClosed);

        /// <summary>Blocked and NotReady both mean "nothing was shown" to a game; Failed stays Failed.</summary>
        public static AdOutcome Map(AdResult result)
        {
            switch (result)
            {
                case AdResult.Rewarded: return AdOutcome.Rewarded;
                case AdResult.Closed: return AdOutcome.Closed;
                case AdResult.Failed: return AdOutcome.Failed;
                default: return AdOutcome.NotReady;
            }
        }
    }

    /// <summary>"Remove ads" bought in the store: interstitials stop, rewarded ads stay.</summary>
    public sealed class IapAdFreeEntitlement : IAdFreeEntitlement
    {
        private readonly IIapService _store;
        private readonly string _productId;

        public event Action Changed;

        public IapAdFreeEntitlement(IIapService store, string productId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("A product id is required.", nameof(productId));
            _productId = productId;
            _store.StoreChanged += () => Changed?.Invoke();
        }

        public bool AdFree => _store.IsProductOwned(_productId);
    }
}
