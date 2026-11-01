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
    public sealed class NexusAdsAdapter : IAdNetworkAdapter, IAdOutcomeAdapter, IAdReadinessAdapter, IAdConsentAdapter, IDisposable
    {
        public const string ProviderName = "nexus-ads";

        private readonly AdController _controller;
        private Action _onInitialized;
        private bool _disposed;

        public event Action ReadyChanged;

        public AdController Controller => _controller;

        public NexusAdsAdapter(AdController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _controller.ReadyChanged += OnReadyChanged;
        }

        /// <summary>Detaches from the controller; the bridge disposes the adapter a later Attach for the same controller replaces (issue #177).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _controller.ReadyChanged -= OnReadyChanged;
            _onInitialized = null;
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

        public void ShowRewarded(string placement, Action<bool> onCompleted) => _controller.Show(placement, result =>
        {
            onCompleted?.Invoke(result == AdResult.Rewarded);
            if (result == AdResult.Rewarded) AcknowledgeShownReward();
        });

        public void RequestReward(string placement, Action<AdOutcome> onOutcome) => _controller.Show(placement, result =>
        {
            onOutcome?.Invoke(Map(result));
            if (result == AdResult.Rewarded) AcknowledgeShownReward();
        });

        // The game's callback returned: the receipt written for this show is paid. A callback that threw leaves it, so the next start
        // pays it; without this every reward stayed pending and was paid again at the next start (issue #178).
        private void AcknowledgeShownReward()
        {
            var receipt = _controller.PendingReward;
            if (receipt.HasValue) _controller.AcknowledgeReward(receipt.Value.Id);
        }

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

    /// <summary>
    /// "Remove ads" bought in the store: interstitials stop, rewarded ads stay. While the store has not said whether the
    /// product is owned (offline, or before its purchase fetch answered), interstitials stop too, so a paying player never
    /// sees one (issue #183). <see cref="Dispose"/> unsubscribes it from the store.
    /// </summary>
    public sealed class IapAdFreeEntitlement : IAdFreeEntitlement, IDisposable
    {
        private readonly IIapService _store;
        private readonly string _productId;
        private readonly Action _forward;

        public event Action Changed;

        public IapAdFreeEntitlement(IIapService store, string productId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("A product id is required.", nameof(productId));
            _productId = productId;
            _forward = () => Changed?.Invoke();
            _store.StoreChanged += _forward;
        }

        public bool AdFree => _store.GetOwnership(_productId) != Ownership.NotOwned;

        public void Dispose() => _store.StoreChanged -= _forward;
    }
}
