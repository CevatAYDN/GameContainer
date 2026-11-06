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
        private int _rewardSequence;
        private bool _fullScreenOpen, _killSwitch, _disposed, _lockHeld;
        private int _initFailures;
        private IDisposable _initRetry;
        // A UI that polls IsReady every frame (an OnGUI button asks twice) instead of following ReadyChanged would cross
        // into the SDK each time, which logs every IsAdReady in test mode: an answer is reused this long; Show and Preload ask live.
        private const double ProviderReadyWindowSeconds = 0.1;
        // A time rule of the guard that runs out changes readiness with no provider or app event, so a wake raises ReadyChanged then.
        private IDisposable _pacingWake;
        private double _pacingWakeAt;
        private long _pacingDay;
        // the wake waits this much past the window's end: a timer that resumes a hair early would find the guard still blocking
        private const double PacingWakeMarginSeconds = 0.01;
        // the clock gives the UTC day, not the time to midnight: while a day cap blocks, the day is compared this often
        private const double DayCapCheckSeconds = 60;
        private readonly double[] _providerReadyAt = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity }; // per AdFormat
        private readonly bool[] _providerReady = new bool[3];
        // The open show's watchdog (issue #180): an SDK that drops its show callback held the full-screen flag for good.
        private Action<string> _expireOpenShow; // ends the open show as failed; null when no show is open
        private AdFormat _openShowFormat;
        private bool _openShowOpened, _openShowLeftFocus;
        private IDisposable _showDeadline, _answerWait;

        /// <summary>How long a show may go unanswered while the app is visible before it is ended as failed (issue #180).</summary>
        public const double ShowAnswerSeconds = 10;
        /// <summary>How long the consent flow or the SDK start may go unanswered before an <see cref="AdEventKind.Unanswered"/> row.</summary>
        public const double AnswerWaitSeconds = 30;

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
            IConsentFlow consent = null, IAdFreeEntitlement adFree = null, IAdsRemoteConfig remote = null, IAdTouchInput touch = null)
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
            Guard = new PolicyGuard(config, config.Policy, visibility, _session, touch);
            if (config.Uses(AdFormat.Rewarded)) _slots[AdFormat.Rewarded] = new Slot();
            if (config.Uses(AdFormat.Interstitial)) _slots[AdFormat.Interstitial] = new Slot();
        }

        /// <summary>Takes the mediation lock, runs consent, starts the SDK and preloads. Safe to call once.</summary>
        public void Start()
        {
            if (State != Phase.Stopped || _disposed || _provider is NoAdsProvider) return; // a host that runs no ads stays Stopped (issue #165)
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
            if (_consent != null) _consent.Changed += OnConsentChanged;
            if (_adFree != null) _adFree.Changed += OnAdFreeChanged;
            if (_remote != null)
            {
                _remote.Changed += OnRemoteChanged;
                OnRemoteChanged();
            }
            if (_consent != null && !_consent.Completed)
            {
                State = Phase.AwaitingConsent;
                WaitForAnswer("the consent flow has not answered in {0} s: every show is blocked until it does (a consent form may still be open)");
                _consent.Request(() => OnMain(OnConsentCompleted));
                return;
            }
            if (_consent != null && !_consent.CanRequestAds) { RefuseConsent(); return; }
            InitializeProvider();
        }

        /// <summary>True when a show now would reach the screen: loaded, visible, not paced out, not switched off.</summary>
        public bool IsReady(string placement)
        {
            var declared = _config.Find(placement);
            if (declared == null || State != Phase.Running || _killSwitch || AdFreeCovers(declared.Format)) return false;
            // a banner has no load slot: the guard alone decides, as it does for ShowBanner (#282)
            if (declared.Format == AdFormat.Banner) return Guard.Check(placement, AdFormat.Banner, new GuardState(_fullScreenOpen, false)).Allowed;
            if (!_slots.TryGetValue(declared.Format, out var slot) || slot.State != SlotState.Ready || !ProviderReady(declared.Format)) return false;
            var verdict = Guard.Check(placement, declared.Format, new GuardState(_fullScreenOpen, false));
            WakeAfterTouch(verdict, declared);
            return verdict.Allowed;
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
            // A loaded ad the provider no longer has (it expired, or the SDK dropped it when consent changed) reloads: nothing else
            // moves a Ready slot, so the format stayed dead until a restart (issue #175). The reload's OnLoaded raises ReadyChanged.
            if (!ready && _slots.TryGetValue(format, out var slot) && slot.State == SlotState.Ready)
            {
                slot.State = SlotState.Idle;
                _telemetry.Record(new AdEvent(AdEventKind.Expired, format, ""));
                Preload(format);
            }
            return ready;
        }

        /// <summary>
        /// Shows the placement. <paramref name="onResult"/> runs exactly once, on the main thread.
        /// <see cref="AdResult.Blocked"/> comes with a <see cref="Violation"/>; <see cref="AdResult.NotReady"/> is silent.
        /// When the provider's show throws, the screen and the slot are released, <paramref name="onResult"/> gets
        /// <see cref="AdResult.Failed"/>, the format reloads, and the exception then reaches the caller.
        /// </summary>
        public void Show(string placement, Action<AdResult> onResult)
        {
            var declared = _config.Find(placement);
            // one banner path: this one showed without recording the show, so the placement's cooldown and caps held for ShowBanner only (issue #327)
            if (declared?.Format == AdFormat.Banner) { ShowBanner(placement, BannerPosition.Bottom, onResult); return; }
            var format = declared?.Format ?? AdFormat.Interstitial;
            if (SwitchedOff(declared, onResult)) return;
            var verdict = Guard.Check(placement, format, new GuardState(_fullScreenOpen, State == Phase.AwaitingConsent));
            if (!verdict.Allowed)
            {
                Raise(new PolicyViolation(verdict.Rule, placement ?? "", format, verdict.Reason));
                WakeAfterTouch(verdict, declared);
                onResult?.Invoke(AdResult.Blocked);
                return;
            }
            if (State != Phase.Running)
            {
                NotReady(declared, State.ToString());
                onResult?.Invoke(AdResult.NotReady);
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
            bool answered = false, expired = false;
            void Answer(AdResult result, string detail)
            {
                if (answered)
                {
                    // The watchdog ended the show and the game heard Failed: a reward the SDK pays after all is kept as the
                    // pending receipt, which the game pays at its next start. Any other late answer, or a second one, pays nothing.
                    if (expired && result == AdResult.Rewarded)
                    {
                        expired = false;
                        Isolated(() => WriteReceipt(placement), format, placement, "writing the reward receipt");
                        Isolated(() => _telemetry.Record(new AdEvent(AdEventKind.Rewarded, format, placement, "after the show was ended as failed: kept as the pending reward")),
                            format, placement, "the telemetry sink");
                    }
                    return;
                }
                answered = true;
                _expireOpenShow = null;
                _showDeadline?.Dispose();
                _showDeadline = null;
                _fullScreenOpen = false;
                slot.State = SlotState.Idle;
                // Each step is isolated, so the game's result arrives exactly once whatever a subscriber or sink throws (issue #176).
                // The receipt is durable before the game hears of the reward: a kill in between keeps it.
                if (result == AdResult.Rewarded) Isolated(() => WriteReceipt(placement), format, placement, "writing the reward receipt");
                if (result == AdResult.Rewarded || result == AdResult.Closed) Isolated(() => _session.RecordShown(declared), format, placement, "recording the show");
                Isolated(() => _telemetry.Record(new AdEvent(result == AdResult.Failed ? AdEventKind.ShowFailed : result == AdResult.Rewarded ? AdEventKind.Rewarded : AdEventKind.Closed, format, placement, detail)),
                    format, placement, "the telemetry sink");
                Isolated(() => AdClosed?.Invoke(format, result), format, placement, "an AdClosed subscriber");
                Preload(format);
                RaiseReadyChanged();
                onResult?.Invoke(result);
            }
            _expireOpenShow = detail => { expired = true; Answer(AdResult.Failed, detail); };
            _openShowFormat = format;
            _openShowOpened = _openShowLeftFocus = false;
            ArmShowDeadline();
            try { _provider.Show(format, placement, result => OnMain(() => Answer(result, null))); }
            catch (Exception ex)
            {
                // the open flag would block every later full-screen show (NoStackedAds) and the slot would never reload (#151)
                Answer(AdResult.Failed, "provider threw " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }

        private void Isolated(Action step, AdFormat format, string placement, string what)
        {
            try { step(); }
            catch (Exception ex) { RecordHandlerFailure(format, placement, what, ex); }
        }

        private void RecordHandlerFailure(AdFormat format, string placement, string what, Exception ex)
        {
            try { _telemetry.Record(new AdEvent(AdEventKind.HandlerFailed, format, placement ?? "", what + " threw " + ex.GetType().Name + ": " + ex.Message)); }
            catch (Exception) { } // the telemetry port threw too: nothing is left to report it through (the Unity port guards each sink)
        }

        // The window counts while the app is visible: from the request until the ad opens, and again from the app's return when
        // the ad took the focus (Android's ad activity), whose close comes right after. An ad that opened in the game's own window
        // (the editor's simulator, which waits for a button) gets no window: only the player ends it.
        // shortcut: an ad that opens without taking the focus and whose close never comes still holds the screen (an iOS ad view
        // controller may be one; gate 12 runs Android only). Add a long cap when a device shows one.
        private void ArmShowDeadline()
        {
            _showDeadline?.Dispose();
            _showDeadline = null;
            if (_expireOpenShow == null || !_visibility.Visible || (_openShowOpened && !_openShowLeftFocus)) return;
            _showDeadline = _scheduler.Schedule(ShowAnswerSeconds, () =>
            {
                _showDeadline = null;
                _expireOpenShow?.Invoke("no answer from the provider in " + ShowAnswerSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " s with the app visible: ended as failed");
            });
        }

        public void HideBanner() => _provider.HideBanner();

        /// <summary>
        /// Shows a banner placement at a position; same guard and NotReady rules as <see cref="Show"/>. A call that reaches
        /// the provider counts as a show of the placement, so its cooldown and caps also space a call that only moves the banner.
        /// </summary>
        public void ShowBanner(string placement, BannerPosition position, Action<AdResult> onResult = null)
        {
            var declared = _config.Find(placement);
            if (SwitchedOff(declared, onResult)) return;
            var verdict = Guard.Check(placement, AdFormat.Banner, new GuardState(_fullScreenOpen, State == Phase.AwaitingConsent));
            if (!verdict.Allowed) { Raise(new PolicyViolation(verdict.Rule, placement ?? "", AdFormat.Banner, verdict.Reason)); onResult?.Invoke(AdResult.Blocked); return; }
            if (State != Phase.Running) { NotReady(declared, State.ToString()); onResult?.Invoke(AdResult.NotReady); return; }
            _lastPlacement[AdFormat.Banner] = placement;
            _provider.ShowBanner(placement, position);
            // the show counts for the placement's cooldown and caps, and a wake is armed for its cooldown's end (#282)
            Isolated(() => _session.RecordShown(declared), AdFormat.Banner, placement, "recording the show");
            ArmPacingWake();
            onResult?.Invoke(AdResult.Closed);
        }

        /// <summary>Attaches (or replaces) the ad-free entitlement after construction; the bridge wires it once the store exists.</summary>
        public void SetAdFreeEntitlement(IAdFreeEntitlement adFree)
        {
            if (_adFree != null) _adFree.Changed -= OnAdFreeChanged;
            _adFree = adFree;
            if (_adFree != null) _adFree.Changed += OnAdFreeChanged;
            OnAdFreeChanged();
        }

        /// <summary>Attaches (or replaces) the remote configuration after construction and applies it at once.</summary>
        public void SetRemoteConfig(IAdsRemoteConfig remote)
        {
            if (_remote != null) _remote.Changed -= OnRemoteChanged;
            _remote = remote;
            if (_remote != null) { _remote.Changed += OnRemoteChanged; OnRemoteChanged(); }
            else if (_killSwitch) { SetKillSwitch(false); RaiseReadyChanged(); }
        }

        /// <summary>
        /// The reward the game has not acknowledged yet: written to the store before the show result is
        /// delivered, cleared by <see cref="AcknowledgeReward"/>. A game checks it at start-up and pays
        /// a reward the previous run was killed in the middle of. Null when nothing is pending.
        /// </summary>
        public RewardReceipt? PendingReward => RewardReceipts.Pending(_store);

        /// <summary>Clears the pending receipt when its id matches; any other id leaves it untouched.</summary>
        public void AcknowledgeReward(string receiptId) => RewardReceipts.Acknowledge(_store, receiptId);

        private void WriteReceipt(string placement)
        {
            _rewardSequence++;
            string id = _clock.UtcDay + "-" + _rewardSequence + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var replaced = RewardReceipts.Pending(_store);
            RewardReceipts.Write(_store, new RewardReceipt(id, placement));
            // the next start pays only the newest receipt: an earlier reward nobody acknowledged is lost, and said so (issue #178)
            if (replaced.HasValue)
                _telemetry.Record(new AdEvent(AdEventKind.RewardReplaced, AdFormat.Rewarded, placement, "receipt " + replaced.Value.Id + " ('" + replaced.Value.Placement
                    + "') was never acknowledged and is replaced by " + id + ": acknowledge each reward once it is paid"));
        }

        public bool PrivacyOptionsRequired => _consent != null && _consent.PrivacyOptionsRequired;

        public void ShowPrivacyOptions(Action onClosed)
        {
            if (_consent == null) { onClosed?.Invoke(); return; }
            _consent.ShowPrivacyOptions(() => OnMain(() =>
            {
                ReconcileConsent();
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
            if (_consent != null) _consent.Changed -= OnConsentChanged;
            if (_adFree != null) _adFree.Changed -= OnAdFreeChanged;
            if (_remote != null) _remote.Changed -= OnRemoteChanged;
            foreach (var slot in _slots.Values) { slot.Retry?.Dispose(); slot.Retry = null; }
            _initRetry?.Dispose();
            _pacingWake?.Dispose();
            _pacingWake = null;
            _showDeadline?.Dispose();
            _showDeadline = null;
            _answerWait?.Dispose();
            _answerWait = null;
            if (_lockHeld) MediationLock.Release(_provider.Name);
            State = Phase.Stopped;
        }

        // ----- consent and initialization

        private void OnConsentChanged() => OnMain(ReconcileConsent);

        private void ReconcileConsent()
        {
            // A pending consent request decides in OnConsentCompleted.
            if (_disposed || State == Phase.Stopped || State == Phase.AwaitingConsent) return;
            // A start still out re-reads consent in OnInitAnswered.
            if (_consent.CanRequestAds) { if (State == Phase.ConsentRefused) InitializeProvider(); }
            else if (State == Phase.Running || State == Phase.InitFailed) RefuseConsent();
            RaiseReadyChanged();
        }

        private void OnConsentCompleted()
        {
            if (_disposed) return;
            _answerWait?.Dispose();
            _answerWait = null;
            if (_consent.CanRequestAds) InitializeProvider();
            else RefuseConsent();
        }

        private void RefuseConsent()
        {
            State = Phase.ConsentRefused;
            // A withdrawal stops a running controller: the banner goes, and every slot not showing is forgotten (a load still out
            // may never answer), so the next grant's preload asks the provider again, which may still hold the ad or, as AdMob
            // does, dropped it at the withdrawal (issue #328)
            _provider.HideBanner();
            foreach (var slot in _slots.Values)
            {
                if (slot.State == SlotState.Showing) continue;
                slot.Retry?.Dispose();
                slot.Retry = null;
                slot.State = SlotState.Idle;
            }
            _telemetry.Record(new AdEvent(AdEventKind.ConsentRefused, AdFormat.Interstitial, "", "the consent flow allows no ad request; no ads until the player changes it in the privacy options"));
            RaiseReadyChanged();
        }

        /// <summary>Records one <see cref="AdEventKind.Unanswered"/> row if the consent flow or the SDK start is still out after
        /// <see cref="AnswerWaitSeconds"/>; the wait itself goes on (issue #180).</summary>
        private void WaitForAnswer(string detail)
        {
            _answerWait?.Dispose();
            var phase = State;
            _answerWait = _scheduler.Schedule(AnswerWaitSeconds, () =>
            {
                _answerWait = null;
                if (!_disposed && State == phase)
                    _telemetry.Record(new AdEvent(AdEventKind.Unanswered, AdFormat.Interstitial, "",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture, detail, AnswerWaitSeconds)));
            });
        }

        private void InitializeProvider()
        {
            State = Phase.Initializing;
            WaitForAnswer("the SDK has not answered its start in {0} s: no ad loads until it does (an SDK may wait for a network)");
            // a throwing SDK start left the controller Initializing for good, with no start line and no Started event; it is a
            // failed start, retried with the back-off (issue #195)
            try { _provider.Initialize(_config.Provider, ok => OnMain(() => OnInitAnswered(ok, null))); }
            catch (Exception ex) { if (State == Phase.Initializing) OnInitAnswered(false, "provider threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void OnInitAnswered(bool ok, string thrown)
        {
            if (_disposed) return;
            _answerWait?.Dispose();
            _answerWait = null;
            // consent withdrawn in the privacy options while the start was out: no ad loads and no start is retried until the
            // player grants again, whose start a provider already started answers at once (issue #328)
            if (_consent != null && !_consent.CanRequestAds) { RefuseConsent(); return; }
            if (ok)
            {
                _telemetry.Record(new AdEvent(AdEventKind.Initialized, AdFormat.Interstitial, "", _initFailures == 0 ? null : "after " + _initFailures + " failed start(s)"));
                _initFailures = 0;
                State = Phase.Running;
                foreach (var format in _slots.Keys) Preload(format);
            }
            else
            {
                State = Phase.InitFailed;
                _initFailures++;
                double delay = RetryPolicy.DelaySeconds(_initFailures);
                _telemetry.Record(new AdEvent(AdEventKind.InitFailed, AdFormat.Interstitial, "", (thrown != null ? thrown + "; " : "") + "attempt " + _initFailures + ", retry in " + delay.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s"));
                _initRetry?.Dispose();
                _initRetry = _scheduler.Schedule(delay, () => { if (!_disposed && State == Phase.InitFailed) InitializeProvider(); });
            }
            RaiseReadyChanged();
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
            // a throwing SDK load left the slot Loading for good; it is a failed load, retried with the back-off (issue #176)
            try { _provider.Load(format); }
            catch (Exception ex) { OnLoadFailed(format, "provider threw " + ex.GetType().Name + ": " + ex.Message); }
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
            if (_expireOpenShow != null)
            {
                if (!visible) _openShowLeftFocus = true;
                ArmShowDeadline(); // stops while the app is away, restarts in full when it returns
            }
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

        private void OnOpened(AdFormat format) => OnMain(() =>
        {
            if (_disposed) return;
            _fullScreenOpen = true;
            if (_expireOpenShow != null && format == _openShowFormat && !_openShowOpened)
            {
                _openShowOpened = true;
                ArmShowDeadline(); // opened in the game's window: no window until the app leaves and returns
            }
            _telemetry.Record(new AdEvent(AdEventKind.Shown, format, PlacementOf(format)));
            AdOpened?.Invoke(format);
        });

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
            SetKillSwitch(_remote.KillSwitch);
            for (int i = 0; i < AdPolicy.Keys.All.Length; i++)
                if (_remote.TryGetDouble(AdPolicy.Keys.All[i], out double value)) Guard.Tighten(AdPolicy.Keys.All[i], value);
            RaiseReadyChanged();
        });

        // ----- helpers

        // The ad-free entitlement removes interstitials and banners; rewarded ads stay, since the player asks for them (issue #195).
        private bool AdFreeCovers(AdFormat format) => format != AdFormat.Rewarded && _adFree != null && _adFree.AdFree;

        // The kill switch and the ad-free entitlement answer NotReady without a violation, so they come before the guard: a paced
        // ad-free interstitial raised a PolicyViolation (issue #195). An undeclared placement still reaches the guard's UnknownPlacement.
        private bool SwitchedOff(AdPlacement declared, Action<AdResult> onResult)
        {
            if (declared == null || !(_killSwitch || AdFreeCovers(declared.Format))) return false;
            NotReady(declared, _killSwitch ? "kill switch" : "ad-free");
            onResult?.Invoke(AdResult.NotReady);
            return true;
        }

        private void OnAdFreeChanged()
        {
            if (_adFree != null && _adFree.AdFree) _provider.HideBanner(); // a banner on screen goes once the player owns ad-free
            RaiseReadyChanged();
        }

        private void NotReady(AdPlacement placement, string why)
            => _telemetry.Record(new AdEvent(AdEventKind.NotReady, placement?.Format ?? AdFormat.Interstitial, placement?.Name ?? "", why));

        private void Raise(PolicyViolation violation)
        {
            _telemetry.Record(new AdEvent(AdEventKind.Violation, violation.Format, violation.Placement, violation.Rule + ": " + violation.Reason));
            Violation?.Invoke(violation);
        }

        // each subscriber on its own: one that throws (a destroyed view's) leaves the later ones told (#236)
        // Every flip is recorded, which the failure log writes in every build (#234). Turning it off loads again what it stopped:
        // removing the remote configuration (SetRemoteConfig(null)) cleared it without an event or a load (#234).
        private void SetKillSwitch(bool kill)
        {
            if (kill == _killSwitch) return;
            _killSwitch = kill;
            _telemetry.Record(new AdEvent(AdEventKind.KillSwitch, AdFormat.Interstitial, "", kill ? "on" : "off"));
            if (kill) _provider.HideBanner(); // a banner on screen goes with the rest (issue #195)
            else if (State == Phase.Running) foreach (var format in _slots.Keys) Preload(format);
        }

        private void RaiseReadyChanged()
        {
            var handlers = ReadyChanged;
            if (handlers != null)
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception ex) { RecordHandlerFailure(AdFormat.Interstitial, "", "a ReadyChanged subscriber", ex); }
                }
            ArmPacingWake();
        }

        // Arms one wake for the loaded placement, or banner, whose time rules end first. Placements held back by anything else are left
        // out: a load, a close, visibility, the kill switch, the ad-free entitlement and a new session raise ReadyChanged themselves.
        private void ArmPacingWake()
        {
            _pacingWake?.Dispose();
            _pacingWake = null;
            if (_disposed || State != Phase.Running || _killSwitch || _fullScreenOpen || !_visibility.Visible) return;
            double soonest = double.PositiveInfinity;
            bool dayCapped = false;
            var placements = _config.Placements;
            for (int i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];
                if (placement.Format != AdFormat.Banner && (!_slots.TryGetValue(placement.Format, out var slot) || slot.State != SlotState.Ready)) continue;
                if ((placement.Format == AdFormat.Interstitial && !placement.NaturalBreak) || AdFreeCovers(placement.Format)) continue;
                if (Guard.SessionCapReached(placement)) continue;
                if (Guard.DayCapReached(placement)) { dayCapped = true; continue; }
                double wait = Guard.SecondsUntilPacingEnds(placement);
                if (wait > 0 && wait < soonest) soonest = wait;
            }
            if (double.IsPositiveInfinity(soonest) && !dayCapped) return;
            _pacingWakeAt = _clock.Seconds + soonest + PacingWakeMarginSeconds;
            _pacingDay = dayCapped ? _clock.UtcDay : long.MinValue; // a day change matters only to a wake armed for a day cap
            _pacingWake = _scheduler.Schedule(Math.Min(soonest + PacingWakeMarginSeconds, dayCapped ? DayCapCheckSeconds : double.PositiveInfinity), OnPacingWake);
        }

        // The touch wait is the one time rule that starts at no event of the controller's, so no wake was armed for its end:
        // the IsReady or Show that meets it arms one, and ReadyChanged follows when the wait ends (issue #126). A wake already
        // armed for that moment is kept, so asking again does not schedule again.
        private void WakeAfterTouch(in PolicyVerdict verdict, AdPlacement placement)
        {
            if (verdict.Allowed || verdict.Rule != PolicyRules.InterstitialAfterTouch) return;
            if (_pacingWake != null && _pacingWakeAt <= _clock.Seconds + Guard.SecondsUntilPacingEnds(placement) + 2 * PacingWakeMarginSeconds) return;
            ArmPacingWake();
        }

        private void OnPacingWake()
        {
            _pacingWake = null;
            if (_disposed || State != Phase.Running) return;
            if (_clock.Seconds >= _pacingWakeAt || (_pacingDay != long.MinValue && _clock.UtcDay != _pacingDay)) RaiseReadyChanged();
            else ArmPacingWake();
        }

        private void OnMain(Action action)
        {
            if (_mainThread.IsCurrent) action();
            else _mainThread.Post(action);
        }
    }
}
