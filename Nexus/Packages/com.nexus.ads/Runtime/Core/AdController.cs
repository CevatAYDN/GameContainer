using System;
using System.Collections.Generic;

namespace Nexus.Ads
{
    /// <summary>
    /// The ad state machine over one <see cref="IAdProvider"/>: consent first, then the SDK, then one
    /// loaded ad per format that placements use; a show hands the slot to the SDK and reloads it on
    /// close; a failed load retries with <see cref="RetryPolicy"/> while the app is visible. Every
    /// show passes <see cref="PolicyGuard"/>; a block raises <see cref="Violation"/>. The reward is
    /// delivered once per show and only when the provider answers <see cref="AdResult.Rewarded"/>.
    /// Every provider callback is marshalled to <see cref="IMainThread"/>.
    /// </summary>
    public sealed class AdController : IDisposable
    {
        private enum SlotState { Idle, Loading, Ready, Showing, Retrying }

        private sealed class Slot
        {
            public SlotState State;
            public int Failures;
            public IDisposable Retry;
        }

        public enum Phase { Stopped, AwaitingConsent, Initializing, Running, ConsentRefused, InitFailed }

        private readonly AdsConfig _config;
        private readonly IAdProvider _provider;
        private readonly IAdClock _clock;
        private readonly IAppVisibility _visibility;
        private readonly IMainThread _mainThread;
        private readonly IAdScheduler _scheduler;
        private readonly IAdTelemetry _telemetry;
        private readonly IConsentFlow _consent;
        private readonly IAdFreeEntitlement _adFree;
        private readonly IAdsRemoteConfig _remote;
        private readonly SessionTracker _session;
        private readonly Dictionary<AdFormat, Slot> _slots = new Dictionary<AdFormat, Slot>();
        private bool _fullScreenOpen, _killSwitch, _disposed, _lockHeld;
        private int _initFailures;
        private IDisposable _initRetry;

        public PolicyGuard Guard { get; }
        public Phase State { get; private set; } = Phase.Stopped;
        public bool KillSwitch => _killSwitch;
        public string ProviderName => _provider.Name;

        public event Action ReadyChanged;
        public event Action<AdFormat> AdOpened;
        public event Action<AdFormat, AdResult> AdClosed;
        public event Action<AdRevenue> Revenue;
        public event Action<PolicyViolation> Violation;

        public AdController(AdsConfig config, IAdProvider provider, IAdClock clock, IAppVisibility visibility, IMainThread mainThread,
            IAdScheduler scheduler, IPacingStore store, IAdTelemetry telemetry,
            IConsentFlow consent = null, IAdFreeEntitlement adFree = null, IAdsRemoteConfig remote = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
            _mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            if (store == null) throw new ArgumentNullException(nameof(store));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _consent = consent;
            _adFree = adFree;
            _remote = remote;
            _session = new SessionTracker(clock, store);
            Guard = new PolicyGuard(config, config.Policy, visibility, _session);
            if (config.Uses(AdFormat.Rewarded)) _slots[AdFormat.Rewarded] = new Slot();
            if (config.Uses(AdFormat.Interstitial)) _slots[AdFormat.Interstitial] = new Slot();
        }

        /// <summary>Takes the mediation lock, runs consent, starts the SDK and preloads. Safe to call once.</summary>
        public void Start()
        {
            if (State != Phase.Stopped || _disposed) return;
            if (!MediationLock.TryAcquire(_provider.Name))
            {
                Raise(new PolicyViolation(PolicyRules.SingleMediation, "", AdFormat.Interstitial,
                    "'" + MediationLock.Holder + "' already runs in this session; '" + _provider.Name + "' was not started"));
                return;
            }
            _lockHeld = true;
            _provider.Loaded += OnLoaded;
            _provider.LoadFailed += OnLoadFailed;
            _provider.Opened += OnOpened;
            _provider.Revenue += OnRevenue;
            _visibility.Changed += OnVisibilityChanged;
            if (_adFree != null) _adFree.Changed += RaiseReadyChanged;
            if (_remote != null)
            {
                _remote.Changed += OnRemoteChanged;
                OnRemoteChanged();
            }
            if (_consent != null && !_consent.Completed)
            {
                State = Phase.AwaitingConsent;
                _consent.Request(() => OnMain(OnConsentCompleted));
                return;
            }
            if (_consent != null && !_consent.CanRequestAds) { State = Phase.ConsentRefused; RaiseReadyChanged(); return; }
            InitializeProvider();
        }

        /// <summary>True when a show now would reach the screen: loaded, visible, not paced out, not switched off.</summary>
        public bool IsReady(string placement)
        {
            var declared = _config.Find(placement);
            if (declared == null || State != Phase.Running || _killSwitch) return false;
            if (declared.Format == AdFormat.Interstitial && _adFree != null && _adFree.AdFree) return false;
            if (declared.Format == AdFormat.Banner) return true;
            if (!_slots.TryGetValue(declared.Format, out var slot) || slot.State != SlotState.Ready || !_provider.IsReady(declared.Format)) return false;
            return Guard.Check(placement, declared.Format, new GuardState(_fullScreenOpen, false)).Allowed;
        }

        /// <summary>
        /// Shows the placement. <paramref name="onResult"/> runs exactly once, on the main thread.
        /// <see cref="AdResult.Blocked"/> comes with a <see cref="Violation"/>; <see cref="AdResult.NotReady"/> is silent.
        /// </summary>
        public void Show(string placement, Action<AdResult> onResult)
        {
            var declared = _config.Find(placement);
            var format = declared?.Format ?? AdFormat.Interstitial;
            var verdict = Guard.Check(placement, format, new GuardState(_fullScreenOpen, State == Phase.AwaitingConsent));
            if (!verdict.Allowed)
            {
                Raise(new PolicyViolation(verdict.Rule, placement ?? "", format, verdict.Reason));
                onResult?.Invoke(AdResult.Blocked);
                return;
            }
            if (State != Phase.Running || _killSwitch || (format == AdFormat.Interstitial && _adFree != null && _adFree.AdFree))
            {
                NotReady(declared, _killSwitch ? "kill switch" : State != Phase.Running ? State.ToString() : "ad-free");
                onResult?.Invoke(AdResult.NotReady);
                return;
            }
            if (format == AdFormat.Banner)
            {
                _provider.ShowBanner(placement, BannerPosition.Bottom);
                onResult?.Invoke(AdResult.Closed);
                return;
            }
            var slot = _slots[format];
            if (slot.State != SlotState.Ready || !_provider.IsReady(format))
            {
                NotReady(declared, "nothing loaded");
                if (slot.State == SlotState.Idle) Preload(format);
                onResult?.Invoke(AdResult.NotReady);
                return;
            }
            slot.State = SlotState.Showing;
            _fullScreenOpen = true;
            bool answered = false;
            _provider.Show(format, placement, result => OnMain(() =>
            {
                if (answered) return; // a provider that answers twice pays once
                answered = true;
                _fullScreenOpen = false;
                slot.State = SlotState.Idle;
                if (result == AdResult.Rewarded || result == AdResult.Closed) _session.RecordShown(declared);
                _telemetry.Record(new AdEvent(result == AdResult.Failed ? AdEventKind.ShowFailed : result == AdResult.Rewarded ? AdEventKind.Rewarded : AdEventKind.Closed, format, placement));
                AdClosed?.Invoke(format, result);
                Preload(format);
                RaiseReadyChanged();
                onResult?.Invoke(result);
            }));
        }

        public void HideBanner() => _provider.HideBanner();

        public bool PrivacyOptionsRequired => _consent != null && _consent.PrivacyOptionsRequired;

        public void ShowPrivacyOptions(Action onClosed)
        {
            if (_consent == null) { onClosed?.Invoke(); return; }
            _consent.ShowPrivacyOptions(() => OnMain(() =>
            {
                // Consent may have been withdrawn or granted: a refused provider starts now, a running one just re-reads readiness.
                if (State == Phase.ConsentRefused && _consent.CanRequestAds) InitializeProvider();
                RaiseReadyChanged();
                onClosed?.Invoke();
            }));
        }

        public void OpenTestSuite() => _provider.OpenTestSuite();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _provider.Loaded -= OnLoaded;
            _provider.LoadFailed -= OnLoadFailed;
            _provider.Opened -= OnOpened;
            _provider.Revenue -= OnRevenue;
            _visibility.Changed -= OnVisibilityChanged;
            if (_adFree != null) _adFree.Changed -= RaiseReadyChanged;
            if (_remote != null) _remote.Changed -= OnRemoteChanged;
            foreach (var slot in _slots.Values) { slot.Retry?.Dispose(); slot.Retry = null; }
            _initRetry?.Dispose();
            if (_lockHeld) MediationLock.Release(_provider.Name);
            State = Phase.Stopped;
        }

        // ----- consent and initialization

        private void OnConsentCompleted()
        {
            if (_disposed) return;
            if (_consent.CanRequestAds) InitializeProvider();
            else { State = Phase.ConsentRefused; RaiseReadyChanged(); }
        }

        private void InitializeProvider()
        {
            State = Phase.Initializing;
            _provider.Initialize(_config.Provider, ok => OnMain(() =>
            {
                if (_disposed) return;
                if (ok)
                {
                    _initFailures = 0;
                    State = Phase.Running;
                    foreach (var format in _slots.Keys) Preload(format);
                }
                else
                {
                    State = Phase.InitFailed;
                    _initFailures++;
                    _initRetry?.Dispose();
                    _initRetry = _scheduler.Schedule(RetryPolicy.DelaySeconds(_initFailures), () => { if (!_disposed && State == Phase.InitFailed) InitializeProvider(); });
                }
                RaiseReadyChanged();
            }));
        }

        // ----- loading (the only two callers of IAdProvider.Load: preload and retry; see PolicyRules.NoAutomaticRequests)

        private void Preload(AdFormat format)
        {
            if (State != Phase.Running || _killSwitch) return;
            var slot = _slots[format];
            if (slot.State == SlotState.Loading || slot.State == SlotState.Ready || slot.State == SlotState.Showing) return;
            if (!_visibility.Visible) { slot.State = SlotState.Retrying; return; } // picked up when the app returns
            slot.Retry?.Dispose();
            slot.Retry = null;
            if (_provider.IsReady(format)) { slot.State = SlotState.Ready; RaiseReadyChanged(); return; }
            slot.State = SlotState.Loading;
            _provider.Load(format);
        }

        private void OnLoaded(AdFormat format) => OnMain(() =>
        {
            if (_disposed || !_slots.TryGetValue(format, out var slot) || slot.State == SlotState.Showing) return;
            slot.Failures = 0;
            slot.State = SlotState.Ready;
            _telemetry.Record(new AdEvent(AdEventKind.Loaded, format, ""));
            RaiseReadyChanged();
        });

        private void OnLoadFailed(AdFormat format, string reason) => OnMain(() =>
        {
            if (_disposed || !_slots.TryGetValue(format, out var slot) || slot.State == SlotState.Showing) return;
            slot.Failures++;
            slot.State = SlotState.Retrying;
            bool noFill = reason != null && reason.IndexOf("no fill", StringComparison.OrdinalIgnoreCase) >= 0;
            _telemetry.Record(new AdEvent(noFill ? AdEventKind.NoFill : AdEventKind.LoadFailed, format, "", reason));
            slot.Retry?.Dispose();
            slot.Retry = _visibility.Visible
                ? _scheduler.Schedule(RetryPolicy.DelaySeconds(slot.Failures), () => { slot.Retry = null; if (slot.State == SlotState.Retrying) { slot.State = SlotState.Idle; Preload(format); } })
                : null;
            RaiseReadyChanged();
        });

        private void OnVisibilityChanged(bool visible) => OnMain(() =>
        {
            if (_disposed) return;
            _session.OnVisibilityChanged(visible, Guard.Policy);
            if (!visible)
            {
                foreach (var slot in _slots.Values) { slot.Retry?.Dispose(); slot.Retry = null; }
                return;
            }
            foreach (var pair in _slots)
            {
                if (pair.Value.State == SlotState.Retrying) { pair.Value.State = SlotState.Idle; Preload(pair.Key); }
            }
            RaiseReadyChanged();
        });

        // ----- provider events

        private void OnOpened(AdFormat format) => OnMain(() => { if (!_disposed) { _fullScreenOpen = true; _telemetry.Record(new AdEvent(AdEventKind.Shown, format, "")); AdOpened?.Invoke(format); } });

        private void OnRevenue(AdRevenue revenue) => OnMain(() =>
        {
            if (_disposed) return;
            _telemetry.Record(new AdEvent(AdEventKind.Revenue, revenue.Format, revenue.Placement, revenue.Network + " " + revenue.Value + " " + revenue.Currency));
            Revenue?.Invoke(revenue);
        });

        private void OnRemoteChanged() => OnMain(() =>
        {
            if (_disposed) return;
            bool kill = _remote.KillSwitch;
            if (kill != _killSwitch)
            {
                _killSwitch = kill;
                _telemetry.Record(new AdEvent(AdEventKind.KillSwitch, AdFormat.Interstitial, "", kill ? "on" : "off"));
                if (!kill && State == Phase.Running) foreach (var format in _slots.Keys) Preload(format);
            }
            for (int i = 0; i < AdPolicy.Keys.All.Length; i++)
                if (_remote.TryGetDouble(AdPolicy.Keys.All[i], out double value)) Guard.Tighten(AdPolicy.Keys.All[i], value);
            RaiseReadyChanged();
        });

        // ----- helpers

        private void NotReady(AdPlacement placement, string why)
            => _telemetry.Record(new AdEvent(AdEventKind.NotReady, placement?.Format ?? AdFormat.Interstitial, placement?.Name ?? "", why));

        private void Raise(PolicyViolation violation)
        {
            _telemetry.Record(new AdEvent(AdEventKind.Violation, violation.Format, violation.Placement, violation.Rule + ": " + violation.Reason));
            Violation?.Invoke(violation);
        }

        private void RaiseReadyChanged() => ReadyChanged?.Invoke();

        private void OnMain(Action action)
        {
            if (_mainThread.IsCurrent) action();
            else _mainThread.Post(action);
        }
    }
}
