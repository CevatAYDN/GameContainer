// #412's intent reader is removed at compile time from release assemblies.
#pragma warning disable UAC0009
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

[assembly: InternalsVisibleTo("com.nexus.ads.editor.tests")]
[assembly: InternalsVisibleTo("com.nexus.ads.tests")]
[assembly: InternalsVisibleTo("com.nexus.ads.bridge.nexus.tests")]

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// Builds the ad stack for this process: settings → build kind → provider gate → provider or
    /// simulator → <see cref="AdController"/>. Pauses audio and time while a full-screen ad is open.
    /// Provider packages register their factory with <see cref="RegisterProvider"/> from a
    /// <c>RuntimeInitializeOnLoadMethod</c>; only the provider the settings select is ever created.
    /// Non-Nexus projects talk to <see cref="Ads"/>; Nexus games go through the bridge package.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class AdsBootstrap : MonoBehaviour
    {
        private static readonly Dictionary<AdProviderKind, Func<IAdScheduler, ProviderConfig, IAdProvider>> Factories = new Dictionary<AdProviderKind, Func<IAdScheduler, ProviderConfig, IAdProvider>>();
        // one slot per source the settings can name, so the order of registrations does not matter (issue #181)
        private static Func<ProviderConfig, IConsentFlow> _googleConsent, _gameConsent;

        [Tooltip("Left empty, Resources/NexusAdsSettings is loaded")]
        [SerializeField] private AdsSettings _settings;
        [SerializeField] private bool _pauseAudioDuringAds = true;
        [SerializeField] private bool _pauseTimeDuringAds = true;
        [Tooltip("What the simulator pretends in the editor and on unlisted devices")]
        [SerializeField] private SimulatorScenario _simulatorScenario = SimulatorScenario.Normal;
        [Tooltip("Ad events go to the console in the editor and development builds")]
        [SerializeField] private bool _logEvents = true;
        [Tooltip("Off: nothing reads the advertising id or touches the consent flow and the SDK until the game calls StartAds()")]
        [SerializeField] private bool _startOnAwake = true;
        [Tooltip("Off: the simulator gets no overlay; the game shows its own screen on Simulator.Opened and ends every show with Simulator.CompleteShow")]
        [SerializeField] private bool _simulatorOverlay = true;
        [Tooltip("Off: development builds get no debug panel")]
        [SerializeField] private bool _debugPanel = true;

        private UnityAdTelemetry _telemetry;
        private IConsentFlow _ownedConsent;
        private float _timeScaleBeforeAd = 1f;
        private bool _audioWasPaused;
        // what OnAdOpened changed for the show that is open: a show that never opened (a display failure) restores nothing (issue #174)
        private bool _restoreAudio, _restoreTime;
        private SimulatorOverlay _overlay;
        private bool _startRequested;

        public static AdsBootstrap Instance { get; private set; }

        public AdsSettings Settings => _settings;
        public BuildKind Build { get; private set; }
        public AdPlatform Platform { get; private set; }
        /// <summary>What <see cref="BuildOutlook.Decide"/> decided at the start: the gate, what runs and, for a host that runs nothing, why.</summary>
        public HostDecision Decision { get; private set; }
        public GateResult Gate => Decision.Gate;
        /// <summary>What runs: the SDK, the labelled simulator, or nothing (<see cref="BuildOutlook.Decide"/>).</summary>
        public HostMode Mode => Decision.Mode;
        public IAdProvider Provider { get; private set; }
        public SimulatorProvider Simulator => Mode == HostMode.Simulator ? Provider as SimulatorProvider : null;
        public AdController Controller { get; private set; }
        public UnityAdTelemetry Telemetry => _telemetry;
        /// <summary>The device's advertising id, read only where the gate compares it with the Test tab's list; null elsewhere.</summary>
        public string DeviceAdvertisingId { get; private set; }
        /// <summary>Raised once the controller exists (where the gate needs the advertising id, it is fetched off the main thread first).</summary>
        public event Action Started;

        /// <summary>
        /// Sets the settings and simulator scenario before the component is enabled (tests, code-made bootstraps). With
        /// <paramref name="simulatorOverlay"/> off the simulator gets no overlay: the game shows its own screen on
        /// <see cref="SimulatorProvider.Opened"/> and ends every show with <see cref="SimulatorProvider.CompleteShow"/>, or the
        /// show stays open. With <paramref name="debugPanel"/> off a development build gets no debug panel (issue #132).
        /// </summary>
        public void Configure(AdsSettings settings, SimulatorScenario simulatorScenario = SimulatorScenario.Normal, bool logEvents = true, bool startOnAwake = true,
            bool simulatorOverlay = true, bool debugPanel = true)
        {
            if (Controller != null || _startRequested) throw new InvalidOperationException("Configure before the bootstrap starts.");
            _settings = settings;
            _simulatorScenario = simulatorScenario;
            _logEvents = logEvents;
            _startOnAwake = startOnAwake;
            _simulatorOverlay = simulatorOverlay;
            _debugPanel = debugPanel;
        }

        /// <summary>Tests: stands in for <see cref="DeviceIdentity.FetchAdvertisingId"/>.</summary>
        internal static Action<Action<string>> AdvertisingIdReader = DeviceIdentity.FetchAdvertisingId;

        /// <summary>Tests: the build kind the next Awake reads instead of <see cref="CurrentBuild"/> (the editor gate never picks the SDK).</summary>
        internal static BuildKind? BuildForTests;

        /// <summary>Tests: puts <paramref name="factory"/> in for <paramref name="kind"/> (null removes it) and returns the one it replaced.</summary>
        internal static Func<IAdScheduler, ProviderConfig, IAdProvider> SwapProviderForTests(AdProviderKind kind, Func<IAdScheduler, ProviderConfig, IAdProvider> factory)
        {
            Factories.TryGetValue(kind, out var previous);
            if (factory == null) Factories.Remove(kind); else Factories[kind] = factory;
            return previous;
        }

        /// <summary>Tests: puts <paramref name="google"/> and <paramref name="game"/> in as the registered consent flows (null clears
        /// one) and returns the two it replaced, which a test puts back: com.nexus.ads.admob registers Google's form before the first scene.</summary>
        internal static (Func<ProviderConfig, IConsentFlow> Google, Func<ProviderConfig, IConsentFlow> Game) SwapConsentFlowsForTests(Func<ProviderConfig, IConsentFlow> google, Func<ProviderConfig, IConsentFlow> game)
        {
            var previous = (_googleConsent, _gameConsent);
            _googleConsent = google;
            _gameConsent = game;
            return previous;
        }

        /// <summary>
        /// Starts the ad stack once: reads the advertising id where the gate needs it (<see cref="BuildOutlook.ReadsAdvertisingId"/>),
        /// then builds the provider or the simulator and the controller, which runs the consent flow before the SDK. Awake calls it unless startOnAwake is off; a game that starts ads
        /// later (after its own explanation, never for a player who owns the ad-free product) calls it itself, after Awake.
        /// Later calls do nothing.
        /// </summary>
        public void StartAds()
        {
            if (_startRequested || Instance != this || _settings == null) return;
            _startRequested = true;
            var mainThread = new UnityMainThread();
            if (BuildOutlook.ReadsAdvertisingId(_settings, Build, _settings.ToProviderConfig(Build, Platform, _settings.SelectedMediation)))
                AdvertisingIdReader(id => mainThread.Post(() => StartWith(id, mainThread)));
            else
                mainThread.Post(() => StartWith(null, mainThread));
        }

        /// <summary>
        /// A provider package registers its factory here (from a RuntimeInitializeOnLoadMethod); the bootstrap hands it
        /// its scheduler and the configuration it will be initialized with (a consent flow runs before Initialize).
        /// </summary>
        public static void RegisterProvider(AdProviderKind kind, Func<IAdScheduler, ProviderConfig, IAdProvider> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Factories[kind] = factory;
        }

        public static bool IsProviderRegistered(AdProviderKind kind) => Factories.ContainsKey(kind);

        /// <summary>
        /// Registers the consent flow that runs before a mediation that ships no form (LevelPlay) for the source the settings'
        /// <see cref="AdsSettings.Consent"/> names: com.nexus.ads.admob registers Google's User Messaging Platform as
        /// <see cref="ConsentSource.GoogleUmp"/> before the first scene (issue #63); a game registers its own flow as
        /// <see cref="ConsentSource.GameFlow"/> before <see cref="StartAds"/>. Each source has its own slot, so the order of the
        /// registrations does not matter, and only the flow the settings name runs (issue #181). A null factory clears that source.
        /// A separate flow returned by the factory belongs to this bootstrap lifetime and is disposed when the bootstrap ends
        /// if it implements <see cref="IDisposable"/>. Directly supplied <see cref="AdController"/> ports remain caller-owned.
        /// </summary>
        public static void RegisterSharedConsentFlow(ConsentSource source, Func<ProviderConfig, IConsentFlow> factory)
        {
            if (source == ConsentSource.GoogleUmp) _googleConsent = factory;
            else if (source == ConsentSource.GameFlow) _gameConsent = factory;
            else throw new ArgumentException("Consent: None names no flow to register; register GoogleUmp or GameFlow.", nameof(source));
        }

        /// <summary>
        /// The consent flow that runs before <paramref name="provider"/> initializes: for LevelPlay with a flow named in the
        /// settings, the one registered for that source (without it the bootstrap starts no provider, <see cref="BuildOutlook.MissingConsent"/>);
        /// otherwise the provider's own flow (AdMob runs UMP itself; LevelPlay's is a pass-through that shows no form, run with Consent: None).
        /// </summary>
        public static IConsentFlow ConsentFlowFor(AdsSettings settings, IAdProvider provider, ProviderConfig config)
        {
            var registered = settings.ConsentNeedsRegisteredFlow ? (settings.Consent == ConsentSource.GoogleUmp ? _googleConsent : _gameConsent) : null;
            return registered != null ? registered(config) : provider as IConsentFlow;
        }

        /// <summary>
        /// How the start line names the consent flow that runs before the mediation (issue #154): its type and where it came
        /// from. "registered" is the flow the settings name, registered with <see cref="RegisterSharedConsentFlow"/> (Google's UMP
        /// from com.nexus.ads.admob, or the game's own); "provider" is the provider's own flow (LevelPlay's is a pass-through that
        /// shows no form and forwards what the game passed with <c>LevelPlayProvider.SetGdprConsent</c>); "simulator" is the
        /// simulator's; "none" when no ads start.
        /// </summary>
        public static string DescribeConsent(AdsSettings settings, HostMode mode, IConsentFlow consent)
        {
            if (mode == HostMode.Idle || consent == null) return "none";
            string from = mode == HostMode.Simulator ? "simulator" : settings.ConsentNeedsRegisteredFlow ? "registered" : "provider";
            return consent.GetType().Name + "(" + from + ")";
        }

        public static BuildKind CurrentBuild => Application.isEditor ? BuildKind.Editor : Debug.isDebugBuild ? BuildKind.Development : BuildKind.Release;

        public static AdPlatform CurrentPlatform => Application.platform == RuntimePlatform.IPhonePlayer ? AdPlatform.Ios : AdPlatform.Android;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // only this component goes: the game's other components on its GameObject stay (issue #195)
                Debug.LogWarning("[Nexus Ads] a second AdsBootstrap on '" + name + "' was removed; the one on '" + Instance.name + "' keeps running the ads");
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            if (_settings == null) _settings = AdsSettings.Load();
            if (_settings == null) { Debug.LogError("[Nexus Ads] No AdsSettings: create one with Assets > Create > Nexus > Ads Settings (with com.nexus.core, Nexus > Ads > Settings) or assign it on the bootstrap."); return; }
            Build = BuildForTests ?? CurrentBuild;
            Platform = CurrentPlatform;
            if (_startOnAwake) StartAds();
        }

        private void StartWith(string advertisingId, UnityMainThread mainThread)
        {
            if (this == null || Controller != null) return;
            DeviceAdvertisingId = advertisingId;
            var config = _settings.ToConfig(Build, Platform);
            var scheduler = gameObject.GetComponent<UnityAdScheduler>() ?? gameObject.AddComponent<UnityAdScheduler>();
            var visibility = gameObject.GetComponent<UnityAppVisibility>() ?? gameObject.AddComponent<UnityAppVisibility>();
            var touch = gameObject.GetComponent<UnityTouchInput>() ?? gameObject.AddComponent<UnityTouchInput>();
            _telemetry = new UnityAdTelemetry(_logEvents && Build != BuildKind.Release, violations: Build == BuildKind.Release);

            Factories.TryGetValue(_settings.SelectedMediation, out var factory);
            // the consent flow the settings name must be registered, or no ads start (issues #154, #181)
            Decision = BuildOutlook.Decide(_settings, Build, config.Provider, advertisingId, factory != null, _googleConsent != null, _gameConsent != null);
            IConsentFlow consent;
            if (Mode == HostMode.Provider)
            {
                Provider = factory(scheduler, config.Provider);
                consent = ConsentFlowFor(_settings, Provider, config.Provider);
            }
            else
            {
                if (Decision.IdleReason != null) // null for the ad-free release, which runs nothing on purpose
                    Debug.LogError("[Nexus Ads] no ads run: " + Decision.IdleReason);
                else if (Gate.Rule != null)
                    Debug.LogWarning("[Nexus Ads] " + Gate.Rule + ": " + Gate.Reason + "; the simulator runs (labelled, not a real ad)");
                if (Mode == HostMode.Simulator)
                {
                    var simulator = new SimulatorProvider(scheduler, _simulatorScenario) { Interactive = true };
                    if (_simulatorOverlay)
                    {
                        _overlay = gameObject.AddComponent<SimulatorOverlay>();
                        _overlay.Bind(simulator);
                    }
                    else Debug.LogWarning("[Nexus Ads] simulator overlay off: the game ends every simulated show with AdsBootstrap.Simulator.CompleteShow (issue #132)");
                    Provider = simulator;
                    consent = simulator;
                }
                else
                {
                    // nothing can start an idle host's controller, the bridge's Initialize included: every show answers NotReady (issue #165)
                    Provider = new NoAdsProvider();
                    consent = null;
                }
            }

            if (!ReferenceEquals(consent, Provider)) _ownedConsent = consent;
            Controller = new AdController(config, Provider, new UnityAdClock(), visibility, mainThread, scheduler, new PlayerPrefsPacingStore(), _telemetry, consent, touch: touch);
            Controller.ReadyChanged += Ads.RaiseReadyChanged; // before Start, so the first fill reaches subscriptions made before this bootstrap existed (issue #195)
            if (!UnityTouchInput.ReadsPointer) Controller.ReadyChanged += WarnTouchWaitUnread; // remote values arrive with a ReadyChanged
            Controller.AdOpened += OnAdOpened;
            Controller.AdClosed += OnAdClosed;
            // tools/device-acceptance counts "[Nexus Ads] PolicyViolation" lines; keep the token. A release build writes
            // each rule and placement's first block as a warning with the same token (UnityAdTelemetry, issue #164)
            if (Build != BuildKind.Release) Controller.Violation += v => Debug.LogError("[Nexus Ads] PolicyViolation " + v);
            if (!UnityTouchInput.ReadsPointer) WarnTouchWaitUnread();
            Controller.Start(); // an idle host (ad-free, blocked, consent flow or provider missing) stays Stopped: its provider is NoAdsProvider
            if (Build != BuildKind.Release && _debugPanel) gameObject.AddComponent<AdsDebugPanel>().Bind(this);
#if DEVELOPMENT_BUILD && UNITY_ANDROID && !UNITY_EDITOR
            if (Build != BuildKind.Release && Provider is IRewardFaultDebug)
                gameObject.AddComponent<RewardFaultIntent>();
#endif
            // one line per start in every build, no ids: tools/device-acceptance -ExpectAds reads it (issue #52)
            Debug.Log("[Nexus Ads] started mode=" + Mode + " mediation=" + _settings.SelectedMediation + " gate=" + Gate.Decision
                + " rule=" + (Gate.Rule ?? "-") + " testMode=" + (config.Provider.TestMode ? "on" : "off")
                + " consent=" + DescribeConsent(_settings, Mode, consent));
            Started?.Invoke();
        }

        // The touch wait set with no pointer to read would never block; said once, in every build (issue #126).
        private void WarnTouchWaitUnread()
        {
            if (Controller == null || Controller.Guard.Policy.NoInterstitialAfterTouchSeconds <= 0) return;
            Controller.ReadyChanged -= WarnTouchWaitUnread;
            Debug.LogWarning("[Nexus Ads] " + AdPolicy.Keys.NoInterstitialAfterTouchSeconds + " is set but this build reads no pointer (neither the Input Manager"
                + " nor Input System 1.20.0 or newer is enabled): interstitials wait after a touch only when the game calls UnityTouchInput.Touched()");
        }

        private void OnAdOpened(AdFormat format)
        {
            if (_pauseAudioDuringAds && !_restoreAudio) { _audioWasPaused = AudioListener.pause; AudioListener.pause = true; _restoreAudio = true; }
            if (_pauseTimeDuringAds && !_restoreTime) { _timeScaleBeforeAd = Time.timeScale; Time.timeScale = 0f; _restoreTime = true; }
        }

        private void OnAdClosed(AdFormat format, AdResult result)
        {
            RestorePauseState();
            if (_overlay != null) _overlay.enabled = false; // its OnGUI runs only while a show is open (issue #83)
        }

        private void RestorePauseState()
        {
            if (_restoreAudio) { AudioListener.pause = _audioWasPaused; _restoreAudio = false; }
            if (_restoreTime) { Time.timeScale = _timeScaleBeforeAd; _restoreTime = false; }
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            var consent = _ownedConsent;
            _ownedConsent = null;
            try
            {
                try { Controller?.Dispose(); }
                finally
                {
                    try { (consent as IDisposable)?.Dispose(); }
                    finally
                    {
                        try { (Provider as IDisposable)?.Dispose(); }
                        finally { Simulator?.Cancel(); }
                    }
                }
            }
            finally
            {
                RestorePauseState();
                Instance = null;
            }
        }
    }

    /// <summary>
    /// The device's advertising id, which the Test tab's device list and the mediation dashboards use.
    /// Android asks Google Play services off the main thread (the call blocks); everything else
    /// answers at once. Without Play services the device id falls back to Unity's device identifier,
    /// which then has to be the id entered on the Test tab.
    /// </summary>
    public static class DeviceIdentity
    {
        public static void FetchAdvertisingId(Action<string> onFetched)
        {
            if (Application.isEditor) { onFetched("editor"); return; }
#if UNITY_ANDROID
            // Main thread only: read in the lookup thread's catch, it threw and the bootstrap never started (issue #117).
            string fallback = SystemInfo.deviceUniqueIdentifier;
            new Thread(() =>
            {
                string id;
                AndroidJNI.AttachCurrentThread(); // JNI from a thread Unity did not attach aborts natively instead of throwing
                try { id = ReadOrFallback(ReadPlayServicesId, fallback); }
                finally { AndroidJNI.DetachCurrentThread(); }
                onFetched(id);
            }) { IsBackground = true, Name = "Nexus.Ads.AdvertisingId" }.Start();
#else
            onFetched(SystemInfo.deviceUniqueIdentifier);
#endif
        }

        /// <summary>
        /// The id <paramref name="read"/> returns, or <paramref name="fallback"/> when it throws. Runs on the lookup thread,
        /// so it touches no Unity API that only the main thread may call.
        /// </summary>
        internal static string ReadOrFallback(Func<string> read, string fallback)
        {
            try { return read(); }
            catch (Exception ex)
            {
                Debug.LogWarning("[Nexus Ads] the advertising id is unavailable (" + ex.GetType().Name + ": " + ex.Message + "); the gate uses this device's SystemInfo.deviceUniqueIdentifier instead, which a mediation dashboard's test device list does not know");
                return fallback;
            }
        }

#if UNITY_ANDROID
        private static string ReadPlayServicesId()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var client = new AndroidJavaClass("com.google.android.gms.ads.identifier.AdvertisingIdClient"))
            using (var info = client.CallStatic<AndroidJavaObject>("getAdvertisingIdInfo", activity))
                return info.Call<string>("getId");
        }
#endif
    }

    /// <summary>
    /// The static facade for projects without Nexus (the Nexus bridge talks to the controller directly).
    /// Every call is safe before the bootstrap exists: nothing is ready and a show answers NotReady.
    /// </summary>
    public static class Ads
    {
        private static AdController Controller => AdsBootstrap.Instance != null ? AdsBootstrap.Instance.Controller : null;

        public static bool IsReady(string placement) => Controller != null && Controller.IsReady(placement);

        public static void Show(string placement, Action<AdResult> onResult = null)
            => Show(placement, null, onResult);

        /// <summary>Shows with a caller offer token persisted in the earned receipt. Null means no context.</summary>
        public static void Show(string placement, string context, Action<AdResult> onResult)
        {
            var controller = Controller;
            if (controller == null) { onResult?.Invoke(AdResult.NotReady); return; }
            controller.Show(placement, context, onResult);
        }

        public static void HideBanner() => Controller?.HideBanner();

        // the receipt is a PlayerPrefs row, read before the controller exists: before StartAds, or before the posted start ran (issue #178)
        private static readonly PlayerPrefsPacingStore ReceiptStore = new PlayerPrefsPacingStore();

        /// <summary>The oldest unacknowledged reward. Grant/persist, acknowledge, then read again until empty.
        /// Readable before ads start; decoding/storage errors propagate. Stop replay on failure. An unreadable row throws
        /// <see cref="System.IO.InvalidDataException"/> and is preserved: call <see cref="QuarantineUnreadableRewards"/> (#415).</summary>
        public static RewardReceipt? PendingReward => RewardReceipts.Pending(ReceiptStore);

        /// <summary>This live callback's receipt, never the older queue head. Copy it synchronously.</summary>
        public static RewardReceipt? DeliveringReward => Controller?.DeliveringReward;

        public static void AcknowledgeReward(string receiptId) => RewardReceipts.Acknowledge(ReceiptStore, receiptId);

        /// <summary>Moves an unreadable pending-reward row to a quarantine key and empties the queue (#415); like
        /// <see cref="PendingReward"/> it needs no controller. Returns the key that holds the row's exact raw value, or null (nothing
        /// written) when the row is empty or readable. A store failure propagates with the row untouched. The move is logged in
        /// every build; an earned reward quarantines such a row by itself.</summary>
        public static string QuarantineUnreadableRewards()
        {
            string key = RewardReceipts.QuarantineUnreadable(ReceiptStore);
            if (key == null) return null;
            var quarantined = RewardReceipts.QuarantineEvent(AdFormat.Rewarded, "", key);
            // the running bootstrap's telemetry logs it in every build; with none, this is the only line, so it is written here
            var telemetry = AdsBootstrap.Instance != null ? AdsBootstrap.Instance.Telemetry : null;
            if (telemetry != null) telemetry.Record(quarantined);
            else Debug.LogWarning("[Nexus Ads] " + quarantined);
            return key;
        }

        public static bool PrivacyOptionsRequired => Controller != null && Controller.PrivacyOptionsRequired;

        public static void ShowPrivacyOptions(Action onClosed = null)
        {
            var controller = Controller;
            if (controller == null) { onClosed?.Invoke(); return; }
            controller.ShowPrivacyOptions(onClosed);
        }

        // every bootstrap's controller forwards its ReadyChanged here, so a subscription does not depend on when the bootstrap exists (issue #195)
        private static event Action ReadyChanged;

        // each handler on its own, recorded as the controller records its subscribers (#236)
        internal static void RaiseReadyChanged()
        {
            var handlers = ReadyChanged;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex)
                {
                    AdsBootstrap.Instance?.Telemetry?.Record(new AdEvent(AdEventKind.HandlerFailed, AdFormat.Interstitial, "",
                        "an Ads.OnReadyChanged handler threw " + ex.GetType().Name + ": " + ex.Message));
                }
            }
        }

        /// <summary>Calls the handler whenever readiness may have changed, on the bootstrap running now or on one that
        /// starts later, a subscription made before any bootstrap exists included; returns an unsubscribe action.</summary>
        public static Action OnReadyChanged(Action handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ReadyChanged += handler;
            return () => ReadyChanged -= handler;
        }
    }
}
