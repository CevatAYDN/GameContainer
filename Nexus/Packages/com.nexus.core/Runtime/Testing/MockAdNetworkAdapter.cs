using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Test double of an ad network: every placement is ready after Initialize unless a test says otherwise, and a
    /// rewarded ad ends with <see cref="RewardOutcome"/>. Testing kit only (editor and test builds); a player has no
    /// mock ads (#162).
    /// </summary>
    public sealed class MockAdNetworkAdapter : IAdNetworkAdapter, IAdOutcomeAdapter, IAdReadinessAdapter, IAdConsentAdapter
    {
        public event Action ReadyChanged;
        /// <summary>Test switch for the consent capability; a real adapter reads this from its consent SDK.</summary>
        public bool PrivacyOptionsRequired { get; set; }
        public int PrivacyOptionsShown { get; private set; }
        /// <summary>What a ready placement's rewarded ad ends with; a test picks Closed, Failed or NotReady (issue #74).</summary>
        public AdOutcome RewardOutcome { get; set; } = AdOutcome.Rewarded;

        public void RequestReward(string placement, Action<AdOutcome> onOutcome)
        {
            if (!IsRewardedReady(placement)) { onOutcome?.Invoke(AdOutcome.NotReady); return; }
            NexusRuntime.Logger?.Log($"[MockAdAdapter] Showing rewarded: {placement}");
            onOutcome?.Invoke(RewardOutcome);
        }

        public void ShowPrivacyOptions(Action onClosed)
        {
            PrivacyOptionsShown++;
            onClosed?.Invoke();
        }

        // Overrides set by SetInterstitialReady / SetRewardedReady; a placement without one is ready.
        private readonly System.Collections.Generic.Dictionary<string, bool> _interstitialReady = new();
        private readonly System.Collections.Generic.Dictionary<string, bool> _rewardedReady = new();
        private bool _initialized;

        public void Initialize(Action onInitialized)
        {
            _initialized = true;
            // Mock: every placement is ready immediately. Placements are labels, as in the shipped
            // AdMob adapter, so a game's "level_end" must behave like "default".
            onInitialized?.Invoke();
            ReadyChanged?.Invoke();
        }

        public bool IsInterstitialReady(string placement)
        {
            return _initialized && (!_interstitialReady.TryGetValue(placement, out var ready) || ready);
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
            return _initialized && (!_rewardedReady.TryGetValue(placement, out var ready) || ready);
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
            onCompleted?.Invoke(RewardOutcome == AdOutcome.Rewarded);
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
        public void SetInterstitialReady(string placement, bool ready) { _interstitialReady[placement] = ready; ReadyChanged?.Invoke(); }
        public void SetRewardedReady(string placement, bool ready) { _rewardedReady[placement] = ready; ReadyChanged?.Invoke(); }
    }
}
