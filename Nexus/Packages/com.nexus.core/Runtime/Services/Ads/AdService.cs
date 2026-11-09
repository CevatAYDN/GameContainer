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

    /// <summary>One ad impression's revenue as the mediation reports it (issue #135).</summary>
    public readonly struct AdImpression
    {
        public readonly string Network;
        public readonly string Placement;
        /// <summary>"Rewarded", "Interstitial" or "Banner" through the ad family's bridge; null when the caller did not say.</summary>
        public readonly string Format;
        public readonly double Value;
        /// <summary>The ISO 4217 code ("USD", "EUR"); null when the caller did not say.</summary>
        public readonly string Currency;
        /// <summary>The mediation's precision word ("exact", "estimated", "publisher_defined", ...); null when the caller did not say.</summary>
        public readonly string Precision;

        public AdImpression(string network, string placement, string format, double value, string currency, string precision)
        {
            Network = network;
            Placement = placement;
            Format = format;
            Value = value;
            Currency = currency;
            Precision = precision;
        }
    }

    public interface IAdService
    {
        /// <summary>Sets and initializes the network adapter. A replaced adapter's banner is hidden and, when it is
        /// <see cref="IDisposable"/>, it is disposed; its late initialize callback is ignored.</summary>
        /// <exception cref="ObjectDisposedException">The service is disposed; the supplied adapter remains caller-owned.</exception>
        void SetNetworkAdapter(IAdNetworkAdapter adapter);
        void SetInterstitialCooldown(float seconds);
        bool IsInterstitialAvailable(string placement);
        bool IsRewardedAvailable(string placement);
        void ShowInterstitial(string placement, Action onComplete = null);
        void ShowRewarded(string placement, Action<bool> onComplete);
        void ShowBanner(string placement = "default", string position = "bottom");
        void HideBanner();
        /// <summary>(network, revenue, placement) of every impression; <see cref="OnAdImpression"/> also carries the format,
        /// the currency and the precision.</summary>
        event Action<string, double, string> OnImpressionRecorded;
        /// <summary>Every impression with its format, currency and precision, raised with <see cref="OnImpressionRecorded"/>
        /// (issue #135). Log revenue from this one: an AdMob impression's currency is not always USD.</summary>
        event Action<AdImpression> OnAdImpression;

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
        private int _disposeState;
        // Set while the interstitial cooldown may still block; Tick clears it and raises ReadyChanged when the cooldown
        // ends (issue #99). Read without the lock, so an idle frame costs one field read.
        private volatile bool _cooldownWakeArmed;

        public bool IsInitialized => _isInitialized;

        public event Action<string, double, string> OnImpressionRecorded;
        public event Action<AdImpression> OnAdImpression;
        public event Action ReadyChanged;
        private readonly Action _forwardReadyChanged;

        public AdService()
        {
            _forwardReadyChanged = () => { if (Volatile.Read(ref _disposeState) == 0) ReadyChanged?.Invoke(); };
        }

        /// <exception cref="ObjectDisposedException">The service is disposed and cannot register for ticks.</exception>
        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposeState) != 0) throw new ObjectDisposedException(nameof(AdService));
            var ticks = TickService;
            ticks?.RegisterTickable(this);
            if (Volatile.Read(ref _disposeState) != 0)
            {
                ticks?.UnregisterTickable(this);
                throw new ObjectDisposedException(nameof(AdService));
            }
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
                if (_disposeState != 0) throw new ObjectDisposedException(nameof(AdService));
                previous = _adapter;
                _adapter = adapter;
                _isInitialized = false;
            }

            previous?.HideBanner();
            if (previous is IAdReadinessAdapter previousReadiness) previousReadiness.ReadyChanged -= _forwardReadyChanged;
            // a replaced adapter is disposed, as IapService does (#118, #314 R3-05)
            if (previous is IDisposable replaced && !ReferenceEquals(previous, adapter)) replaced.Dispose();
            // Replacement cleanup can dispose this service or install another adapter.
            lock (_lock) { if (_disposeState != 0 || !ReferenceEquals(_adapter, adapter)) return; }
            if (adapter is IAdReadinessAdapter readiness) readiness.ReadyChanged += _forwardReadyChanged;
            bool stillCurrent;
            lock (_lock) { stillCurrent = _disposeState == 0 && ReferenceEquals(_adapter, adapter); }
            if (!stillCurrent)
            {
                if (adapter is IAdReadinessAdapter stale) stale.ReadyChanged -= _forwardReadyChanged;
                return;
            }

            // Initialize OUTSIDE the lock: adapter SDKs may block or re-enter this service
            // (e.g. invoke callbacks synchronously), and holding the lock across that would
            // deadlock any other caller.
            if (adapter != null)
            {
                adapter.Initialize(() =>
                {
                    // a late answer of a replaced adapter does not mark the current one (#314 R3-05)
                    bool current;
                    lock (_lock) { current = _disposeState == 0 && ReferenceEquals(_adapter, adapter); if (current) _isInitialized = true; }
                    if (current) ReadyChanged?.Invoke();
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
                // No adapter: nothing is shown, in every build (#162), and the warning reaches every build's log (#164).
                NexusLog.WarnAlways($"[AdService] No ad network adapter for interstitial '{placement}': nothing shown. Set one with SetNetworkAdapter.");
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
                // No adapter: no reward without an impression, in every build (#162), and the warning reaches every build's log (#164).
                NexusLog.WarnAlways($"[AdService] No ad network adapter for rewarded '{placement}': no reward. Set one with SetNetworkAdapter.");
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

        /// <summary>Raises both impression events with no format, currency or precision.</summary>
        public void RaiseImpression(string network, double revenue, string placement)
            => RaiseImpression(new AdImpression(network, placement, null, revenue, null, null));

        /// <summary>Raises <see cref="OnAdImpression"/>, then <see cref="OnImpressionRecorded"/> (issue #135).</summary>
        public void RaiseImpression(AdImpression impression)
        {
            OnAdImpression?.Invoke(impression);
            OnImpressionRecorded?.Invoke(impression.Network, impression.Value, impression.Placement);
        }

        public override void Dispose()
        {
            IAdNetworkAdapter adapter;
            lock (_lock)
            {
                if (_disposeState != 0) return;
                _disposeState = 1;
                _cooldownWakeArmed = false;
                adapter = _adapter;
                _adapter = null;
                _isInitialized = false;
            }
            try { TickService?.UnregisterTickable(this); }
            finally
            {
                try
                {
                    if (adapter is IAdReadinessAdapter readiness) readiness.ReadyChanged -= _forwardReadyChanged;
                    adapter?.HideBanner();
                }
                finally
                {
                    try { (adapter as IDisposable)?.Dispose(); }
                    finally
                    {
                        _interstitialCooldownSeconds.ClearOnChanged();
                        _lastInterstitialTime.ClearOnChanged();
                        base.Dispose();
                    }
                }
            }
        }
    }
}
