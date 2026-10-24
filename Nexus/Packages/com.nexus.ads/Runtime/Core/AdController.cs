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
        private IAdFreeEntitlement _adFree;
        private IAdsRemoteConfig _remote;
        private readonly SessionTracker _session;
        private readonly IPacingStore _store;
        private readonly Dictionary<AdFormat, Slot> _slots = new Dictionary<AdFormat, Slot>();
        // the placement the game last asked to show per format: provider events carry the format only, and revenue arrives after Closed
        private readonly Dictionary<AdFormat, string> _lastPlacement = new Dictionary<AdFormat, string>();
        private const string PendingRewardKey = "nexus.ads.reward.pending";
        private int _rewardSequence;
        private bool _fullScreenOpen, _killSwitch, _disposed, _lockHeld;
        private int _initFailures;
        private IDisposable _initRetry;
        // UI polls IsReady every frame (an OnGUI button asks twice) and each provider call crossed into the SDK,
        // which logs every IsAdReady in test mode: an answer is reused this long; Show and Preload ask live.
        private const double ProviderReadyWindowSeconds = 0.1;
        private readonly double[] _providerReadyAt = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity }; // per AdFormat
        private readonly bool[] _providerReady = new bool[3];

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
            _store = store ?? throw new ArgumentNullException(nameof(store));
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
            if (!_slots.TryGetValue(declared.Format, out var slot) || slot.State != SlotState.Ready || !ProviderReady(declared.Format)) return false;
            return Guard.Check(placement, declared.Format, new GuardState(_fullScreenOpen, false)).Allowed;
        }

        private bool ProviderReady(AdFormat format)
        {
            double age = _clock.Seconds - _providerReadyAt[(int)format];
            return age >= 0 && age < ProviderReadyWindowSeconds ? _providerReady[(int)format] : ProviderReadyLive(format);
        }

        private bool ProviderReadyLive(AdFormat format)
        {
            bool ready = _provider.IsReady(format);
            _providerReadyAt[(int)format] = _clock.Seconds;
            _providerReady[(int)format] = ready;
            return ready;
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
                _lastPlacement[format] = placement;
                _provider.ShowBanner(placement, BannerPosition.Bottom);
                onResult?.Invoke(AdResult.Closed);
                return;
            }
            var slot = _slots[format];
            if (slot.State != SlotState.Ready || !ProviderReadyLive(format))
            {
                NotReady(declared, "nothing loaded");
                if (slot.State == SlotState.Idle) Preload(format);
                onResult?.Invoke(AdResult.NotReady);
                return;
            }
            slot.State = SlotState.Showing;
            _fullScreenOpen = true;
            _lastPlacement[format] = placement;
            bool answered = false;
            _provider.Show(format, placement, result => OnMain(() =>
            {
                if (answered) return; // a provider that answers twice pays once
                answered = true;
                _fullScreenOpen = false;
                slot.State = SlotState.Idle;
                if (result == AdResult.Rewarded) WriteReceipt(placement); // durable before the game hears of it: a kill in between keeps the reward
                if (result == AdResult.Rewarded || result == AdResult.Closed) _session.RecordShown(declared);
                _telemetry.Record(new AdEvent(result == AdResult.Failed ? AdEventKind.ShowFailed : result == AdResult.Rewarded ? AdEventKind.Rewarded : AdEventKind.Closed, format, placement));
                AdClosed?.Invoke(format, result);
                Preload(format);
                RaiseReadyChanged();
                onResult?.Invoke(result);
            }));
        }

        public void HideBanner() => _provider.HideBanner();

        /// <summary>Shows a banner placement at a position; same guard and NotReady rules as <see cref="Show"/>.</summary>
        public void ShowBanner(string placement, BannerPosition position, Action<AdResult> onResult = null)
        {
            var declared = _config.Find(placement);
            var verdict = Guard.Check(placement, AdFormat.Banner, new GuardState(_fullScreenOpen, State == Phase.AwaitingConsent));
            if (!verdict.Allowed) { Raise(new PolicyViolation(verdict.Rule, placement ?? "", AdFormat.Banner, verdict.Reason)); onResult?.Invoke(AdResult.Blocked); return; }
            if (State != Phase.Running || _killSwitch) { NotReady(declared, _killSwitch ? "kill switch" : State.ToString()); onResult?.Invoke(AdResult.NotReady); return; }
            _lastPlacement[AdFormat.Banner] = placement;
            _provider.ShowBanner(placement, position);
            onResult?.Invoke(AdResult.Closed);
        }

        /// <summary>Attaches (or replaces) the ad-free entitlement after construction; the bridge wires it once the store exists.</summary>
        public void SetAdFreeEntitlement(IAdFreeEntitlement adFree)
        {
            if (_adFree != null) _adFree.Changed -= RaiseReadyChanged;
            _adFree = adFree;
            if (_adFree != null) _adFree.Changed += RaiseReadyChanged;
            RaiseReadyChanged();
        }

        /// <summary>Attaches (or replaces) the remote configuration after construction and applies it at once.</summary>
        public void SetRemoteConfig(IAdsRemoteConfig remote)
        {
            if (_remote != null) _remote.Changed -= OnRemoteChanged;
            _remote = remote;
            if (_remote != null) { _remote.Changed += OnRemoteChanged; OnRemoteChanged(); }
            else if (_killSwitch) { _killSwitch = false; RaiseReadyChanged(); }
        }

        /// <summary>
        /// The reward the game has not acknowledged yet: written to the store before the show result is
        /// delivered, cleared by <see cref="AcknowledgeReward"/>. A game checks it at start-up and pays
        /// a reward the previous run was killed in the middle of. Null when nothing is pending.
        /// </summary>
        public RewardReceipt? PendingReward
        {
            get
            {
                string raw = _store.Get(PendingRewardKey);
                if (string.IsNullOrEmpty(raw)) return null;
                int bar = raw.IndexOf('|');
                return bar <= 0 ? (RewardReceipt?)null : new RewardReceipt(raw.Substring(0, bar), raw.Substring(bar + 1));
            }
        }

        /// <summary>Clears the pending receipt when its id matches; any other id leaves it untouched.</summary>
        public void AcknowledgeReward(string receiptId)
        {
            var pending = PendingReward;
            if (pending.HasValue && pending.Value.Id == receiptId) _store.Set(PendingRewardKey, "");
        }

        private void WriteReceipt(string placement)
        {
            _rewardSequence++;
            string id = _clock.UtcDay + "-" + _rewardSequence + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _store.Set(PendingRewardKey, id + "|" + placement);
        }

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
            if (ProviderReadyLive(format)) { slot.State = SlotState.Ready; RaiseReadyChanged(); return; }
            slot.State = SlotState.Loading;
            _provider.Load(format);
        }

        private void OnLoaded(AdFormat format) => OnMain(() =>
        {
            if (_disposed || !_slots.TryGetValue(format, out var slot) || slot.State == SlotState.Showing) return;
            slot.Failures = 0;
            slot.State = SlotState.Ready;
            _providerReadyAt[(int)format] = double.NegativeInfinity; // a "not ready" cached before the load is stale
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

        private void OnOpened(AdFormat format) => OnMain(() => { if (!_disposed) { _fullScreenOpen = true; _telemetry.Record(new AdEvent(AdEventKind.Shown, format, PlacementOf(format))); AdOpened?.Invoke(format); } });

        private string PlacementOf(AdFormat format) => _lastPlacement.TryGetValue(format, out var placement) ? placement : "";

        private void OnRevenue(AdRevenue revenue) => OnMain(() =>
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(revenue.Placement))
                revenue = new AdRevenue(revenue.Network, PlacementOf(revenue.Format), revenue.Format, revenue.Value, revenue.Currency, revenue.Precision);
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
