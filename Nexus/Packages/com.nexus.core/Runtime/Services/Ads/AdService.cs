using System;
using System.Threading;
using System.Threading.Tasks;
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
    public class AdService : IAdService, INexusService
    {
        private IAdNetworkAdapter _adapter;
        private float _interstitialCooldownSeconds = 30f;
        private float _lastInterstitialTime = -999f;
        private bool _isInitialized;

        public bool IsInitialized => _isInitialized;

        public event Action<string, double, string> OnImpressionRecorded;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void SetNetworkAdapter(IAdNetworkAdapter adapter)
        {
            _adapter = adapter;
            _adapter?.Initialize(() => _isInitialized = true);
        }

        public void SetInterstitialCooldown(float seconds)
        {
            _interstitialCooldownSeconds = Mathf.Max(0f, seconds);
        }

        public bool IsInterstitialAvailable(string placement)
        {
            if (Time.realtimeSinceStartup - _lastInterstitialTime < _interstitialCooldownSeconds)
                return false;

            return _adapter != null ? _adapter.IsInterstitialReady(placement) : true;
        }

        public bool IsRewardedAvailable(string placement)
        {
            return _adapter != null ? _adapter.IsRewardedReady(placement) : true;
        }

        public void ShowInterstitial(string placement, Action onComplete = null)
        {
            if (!IsInterstitialAvailable(placement))
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[AdService] Interstitial not ready or on cooldown for placement: {placement}");
                onComplete?.Invoke();
                return;
            }

            _lastInterstitialTime = Time.realtimeSinceStartup;

            if (_adapter != null)
            {
                _adapter.ShowInterstitial(placement, onComplete);
            }
            else
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.Log($"[AdService Mock] Showing Interstitial for: {placement}");
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
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.Log($"[AdService Mock] Showing Rewarded for: {placement}");
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

        public void OnDispose() { }
    }
}
