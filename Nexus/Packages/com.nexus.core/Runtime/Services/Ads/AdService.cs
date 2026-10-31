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

        /// <summary>Rewarded flow with a structured result; NotReady when nothing is loaded, without calling the adapter.</summary>
        void RequestReward(string placement, Action<AdOutcome> onOutcome);
        /// <summary>Raised when ad readiness may have changed (adapter set and initialized, the adapter reports a change, or the
        /// interstitial cooldown ends while an <see cref="ITickService"/> ticks the service).</summary>
        event Action ReadyChanged;
        /// <summary>True when the adapter's consent flow requires a privacy-options entry in the menu.</summary>
        bool PrivacyOptionsRequired { get; }
        /// <summary>Opens the consent privacy options; completes immediately when the adapter has no consent capability.</summary>
        void ShowPrivacyOptions(Action onClosed = null);
    }

    // Derives from NexusService<IAdService> like every other service, so
    // [Inject] Context/SignalBus are available and OnDispose/Dispose follow the shared
    // lifecycle contract (previously implemented INexusService directly — inconsistent).
    [Preserve]
    public class AdService : NexusService<IAdService>, IAdService, ITickable
    {
        // Without a tick service the end of the interstitial cooldown raises no ReadyChanged (issue #99).
        [OptionalInject] public ITickService TickService { get; set; }

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
        // Set while the interstitial cooldown may still block; Tick clears it and raises ReadyChanged when the cooldown
        // ends (issue #99). Read without the lock, so an idle frame costs one field read.
        private volatile bool _cooldownWakeArmed;

        public bool IsInitialized => _isInitialized;

        public event Action<string, double, string> OnImpressionRecorded;
        public event Action ReadyChanged;
        private readonly Action _forwardReadyChanged;

        public AdService()
        {
            _forwardReadyChanged = () => ReadyChanged?.Invoke();
        }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            TickService?.RegisterTickable(this);
            return default;
        }

        // The end of the cooldown is a readiness change no adapter reports: a UI that follows ReadyChanged
        // kept an interstitial button hidden until something unrelated raised it.
        public void Tick(float deltaTime)
        {
            if (!_cooldownWakeArmed) return;
            lock (_lock)
            {
                if (!_cooldownWakeArmed || Time.realtimeSinceStartup - _lastInterstitialTime.Value < _interstitialCooldownSeconds.Value) return;
                _cooldownWakeArmed = false;
            }
            ReadyChanged?.Invoke();
        }

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
            if (previous is IAdReadinessAdapter previousReadiness) previousReadiness.ReadyChanged -= _forwardReadyChanged;
            if (adapter is IAdReadinessAdapter readiness) readiness.ReadyChanged += _forwardReadyChanged;

            // Initialize OUTSIDE the lock: adapter SDKs may block or re-enter this service
            // (e.g. invoke callbacks synchronously), and holding the lock across that would
            // deadlock any other caller.
            if (adapter != null)
            {
                adapter.Initialize(() =>
                {
                    lock (_lock) { _isInitialized = true; }
                    ReadyChanged?.Invoke();
                });
            }
            else ReadyChanged?.Invoke();
        }

        public void RequestReward(string placement, Action<AdOutcome> onOutcome)
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            if (adapter == null)
            {
                // Same answer as the bool overload: no adapter, no reward (Failed), and its warning (#162).
                ShowRewarded(placement, success => onOutcome?.Invoke(success ? AdOutcome.Rewarded : AdOutcome.Failed));
                return;
            }
            if (!adapter.IsRewardedReady(placement))
            {
                onOutcome?.Invoke(AdOutcome.NotReady);
                return;
            }
            if (adapter is IAdOutcomeAdapter outcomes)
                outcomes.RequestReward(placement, onOutcome);
            else
                adapter.ShowRewarded(placement, success => onOutcome?.Invoke(success ? AdOutcome.Rewarded : AdOutcome.Closed));
        }

        public bool PrivacyOptionsRequired
        {
            get
            {
                IAdNetworkAdapter adapter;
                lock (_lock) { adapter = _adapter; }
                return adapter is IAdConsentAdapter consent && consent.PrivacyOptionsRequired;
            }
        }

        public void ShowPrivacyOptions(Action onClosed = null)
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            if (adapter is IAdConsentAdapter consent) consent.ShowPrivacyOptions(onClosed);
            else onClosed?.Invoke();
        }

        public void SetInterstitialCooldown(float seconds)
        {
            lock (_lock)
            {
                _interstitialCooldownSeconds.Value = Mathf.Max(0f, seconds);
                // After a show, a new cooldown moves the moment the interstitial returns; Tick checks it.
                if (_lastInterstitialTime.Value >= 0f) _cooldownWakeArmed = true;
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
            // No adapter, no ad: the editor and development players get none either (#162).
            return adapter != null && adapter.IsInterstitialReady(placement);
        }

        public bool IsRewardedAvailable(string placement)
        {
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            // Readiness query outside the lock (see IsInterstitialAvailable).
            // No adapter, no ad (#162).
            return adapter != null && adapter.IsRewardedReady(placement);
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
                    if (_interstitialCooldownSeconds.Value > 0f) _cooldownWakeArmed = true;
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
                // No adapter: nothing is shown, in every build (#162).
                NexusRuntime.Logger?.LogWarning($"[AdService] No ad network adapter for interstitial '{placement}': nothing shown. Set one with SetNetworkAdapter.");
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
                // No adapter: no reward without an impression, in every build (#162).
                NexusRuntime.Logger?.LogWarning($"[AdService] No ad network adapter for rewarded '{placement}': no reward. Set one with SetNetworkAdapter.");
                onComplete?.Invoke(false);
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
            TickService?.UnregisterTickable(this);
            _cooldownWakeArmed = false;
            IAdNetworkAdapter adapter;
            lock (_lock) { adapter = _adapter; }
            if (adapter is IAdReadinessAdapter readiness) readiness.ReadyChanged -= _forwardReadyChanged;
            _interstitialCooldownSeconds.ClearOnChanged();
            _lastInterstitialTime.ClearOnChanged();
            base.Dispose();
        }
    }
}
