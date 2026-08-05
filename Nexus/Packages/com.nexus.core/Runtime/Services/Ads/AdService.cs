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

    // Derives from NexusService<IAdService> like every other service, so
    // [Inject] Context/SignalBus are available and OnDispose/Dispose follow the shared
    // lifecycle contract (previously implemented INexusService directly — inconsistent).
    [Preserve]
    [StubService("Replace with AdMob / IronSource adapter before release")]
    public class AdService : NexusService<IAdService>, IAdService
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

        public void SetNetworkAdapter(IAdNetworkAdapter adapter)
        {
            IAdNetworkAdapter previous;
            lock (_lock)
            {
                previous = _adapter;
                _adapter = adapter;
                _isInitialized = false;
            }

            previous?.HideBanner();

            // Initialize OUTSIDE the lock: adapter SDKs may block or re-enter this service
            // (e.g. invoke callbacks synchronously), and holding the lock across that would
            // deadlock any other caller.
            if (adapter != null)
            {
                adapter.Initialize(() =>
                {
                    lock (_lock) { _isInitialized = true; }
                });
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
            IAdNetworkAdapter adapter;
            lock (_lock)
            {
                if (Time.realtimeSinceStartup - _lastInterstitialTime.Value < _interstitialCooldownSeconds.Value)
                    return false;
                adapter = _adapter;
            }

            // Adapter query OUTSIDE the lock: an SDK's readiness call must never hold the
            // service lock (SetNetworkAdapter / ShowBanner from another thread would stall).
            if (adapter != null) return adapter.IsInterstitialReady(placement);

            // Mock mode: if no adapter is set, consider interstitial available only in
            // editor/dev builds. A release build with no adapter must never report an
            // available interstitial, or the player would be shown a fake "reward ad"
            // flow with nothing behind it.
            return UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild;
        }

        public bool IsRewardedAvailable(string placement)
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            // Readiness query outside the lock (see IsInterstitialAvailable).
            if (adapter != null) return adapter.IsRewardedReady(placement);

            // Mock mode: consistent with ShowRewarded — editor/dev builds grant a mock
            // reward without an adapter, so availability must report true there too.
            // A release build without an adapter never reports an available rewarded ad.
            return UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild;
        }

        public void ShowInterstitial(string placement, Action onComplete = null)
        {
            IAdNetworkAdapter adapter;
            bool canShow;
            lock (_lock)
            {
                // Cooldown gate + timestamp are ATOMIC in one lock scope: two concurrent
                // callers cannot both pass (the second observes the timestamp set by the
                // first). The timestamp advances optimistically — a failed readiness below
                // still consumes this attempt, which prevents hammering a broken adapter.
                canShow = Time.realtimeSinceStartup - _lastInterstitialTime.Value >= _interstitialCooldownSeconds.Value;
                if (canShow)
                {
                    _lastInterstitialTime.Value = Time.realtimeSinceStartup;
                }
                adapter = _adapter;
            }

            if (!canShow)
            {
                NexusRuntime.Logger?.LogWarning($"[AdService] Interstitial on cooldown for placement: {placement}");
                onComplete?.Invoke();
                return;
            }

            // Readiness query OUTSIDE the lock (SDK calls must not hold the service lock).
            if (adapter != null)
            {
                if (!adapter.IsInterstitialReady(placement))
                {
                    NexusRuntime.Logger?.LogWarning($"[AdService] Interstitial not ready for placement: {placement}");
                    onComplete?.Invoke();
                    return;
                }
                adapter.ShowInterstitial(placement, onComplete);
            }
            else
            {
                // Mock mode is dev-only (see IsInterstitialAvailable).
                if (!(UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild))
                {
                    NexusRuntime.Logger?.LogWarning($"[AdService] No adapter configured for '{placement}' in a release build.");
                    onComplete?.Invoke();
                    return;
                }
                NexusRuntime.Logger?.Log($"[AdService Mock] Showing Interstitial for: {placement}");
                onComplete?.Invoke();
            }
        }

        public void ShowRewarded(string placement, Action<bool> onComplete)
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }

            if (adapter != null)
            {
                adapter.ShowRewarded(placement, onComplete);
            }
            else
            {
                // A mock reward in a release build is a revenue- and trust-cheat: the player
                // gets free currency without an impression. Only grant mock rewards in
                // editor/dev builds; a release build without an adapter denies the reward.
                if (UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild)
                {
                    NexusRuntime.Logger?.Log($"[AdService Mock] Showing Rewarded for: {placement}");
                    onComplete?.Invoke(true);
                }
                else
                {
                    NexusRuntime.Logger?.LogWarning($"[AdService] No rewarded adapter configured for '{placement}' in a release build. Reward denied.");
                    onComplete?.Invoke(false);
                }
            }
        }

        public void ShowBanner(string placement = "default", string position = "bottom")
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            adapter?.ShowBanner(placement, position);
        }

        public void HideBanner()
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            adapter?.HideBanner();
        }

        public void RaiseImpression(string network, double revenue, string placement)
        {
            OnImpressionRecorded?.Invoke(network, revenue, placement);
        }

        public override void Dispose()
        {
            _interstitialCooldownSeconds.ClearOnChanged();
            _lastInterstitialTime.ClearOnChanged();
            base.Dispose();
        }
    }
}
