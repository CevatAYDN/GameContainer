using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface IAdNetworkAdapter
    {
        void Initialize(Action onInitialized);
        bool IsInterstitialReady(string placement);
        void ShowInterstitial(string placement, Action onClosed);
        bool IsRewardedReady(string placement);
        void ShowRewarded(string placement, Action<bool> onCompleted);
        void ShowBanner(string placement, string position);
        void HideBanner();
    }

    public interface IAdService
    {
        void SetNetworkAdapter(IAdNetworkAdapter adapter);
        void SetInterstitialCooldown(float seconds);
        bool IsInterstitialAvailable(string placement);
        bool IsRewardedAvailable(string placement);
        void ShowInterstitial(string placement, Action onComplete = null);
        void ShowRewarded(string placement, Action<bool> onComplete);
        void ShowBanner(string placement = "default", string position = "bottom");
        void HideBanner();
        event Action<string, double, string> OnImpressionRecorded; // (network, revenue, placement)
    }

    [Preserve]
    [StubService("Replace with AdMob / IronSource adapter before release")]
    public class AdService : IAdService, INexusService
    {
        private IAdNetworkAdapter _adapter;
        // Anti-cheat: cooldown config and the last-show timestamp are XOR-masked in RAM
        // (SecureObservableFloat, matching the project's SecureObservableInt/Long story) so a
        // GameGuardian/CheatEngine memory scan can't zero the cooldown or backdate the timer
        // to spam interstitials. The economic impact is low (revenue is still verified by the
        // ad network) but the masking costs nothing on this call frequency.
        private readonly SecureObservableFloat _interstitialCooldownSeconds = new(30f);
        private readonly SecureObservableFloat _lastInterstitialTime = new(-999f);
        private bool _isInitialized;
        private readonly object _lock = new();

        public bool IsInitialized => _isInitialized;

        public event Action<string, double, string> OnImpressionRecorded;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void SetNetworkAdapter(IAdNetworkAdapter adapter)
        {
            lock (_lock)
            {
                _adapter = adapter;
                _adapter?.Initialize(() => _isInitialized = true);
            }
        }

        public void SetInterstitialCooldown(float seconds)
        {
            lock (_lock)
            {
                _interstitialCooldownSeconds.Value = Mathf.Max(0f, seconds);
            }
        }

        public bool IsInterstitialAvailable(string placement)
        {
            lock (_lock)
            {
                if (Time.realtimeSinceStartup - _lastInterstitialTime.Value < _interstitialCooldownSeconds.Value)
                    return false;

                return _adapter != null ? _adapter.IsInterstitialReady(placement) : true;
            }
        }

        public bool IsRewardedAvailable(string placement)
        {
            lock (_lock)
            {
                return _adapter != null ? _adapter.IsRewardedReady(placement) : true;
            }
        }

        public void ShowInterstitial(string placement, Action onComplete = null)
        {
            lock (_lock)
            {
                if (!IsInterstitialAvailable(placement))
                {
                    NexusRuntime.Logger?.LogWarning($"[AdService] Interstitial not ready or on cooldown for placement: {placement}");
                    onComplete?.Invoke();
                    return;
                }

                _lastInterstitialTime.Value = Time.realtimeSinceStartup;
            }

            if (_adapter != null)
            {
                _adapter.ShowInterstitial(placement, onComplete);
            }
            else
            {
                NexusRuntime.Logger?.Log($"[AdService Mock] Showing Interstitial for: {placement}");
                onComplete?.Invoke();
            }
        }

        public void ShowRewarded(string placement, Action<bool> onComplete)
        {
            if (_adapter != null)
            {
                _adapter.ShowRewarded(placement, onComplete);
            }
            else
            {
                NexusRuntime.Logger?.Log($"[AdService Mock] Showing Rewarded for: {placement}");
                onComplete?.Invoke(true);
            }
        }

        public void ShowBanner(string placement = "default", string position = "bottom")
        {
            _adapter?.ShowBanner(placement, position);
        }

        public void HideBanner()
        {
            _adapter?.HideBanner();
        }

        public void RaiseImpression(string network, double revenue, string placement)
        {
            OnImpressionRecorded?.Invoke(network, revenue, placement);
        }

        public void OnDispose()
        {
            _interstitialCooldownSeconds.ClearOnChanged();
            _lastInterstitialTime.ClearOnChanged();
        }
    }
}
