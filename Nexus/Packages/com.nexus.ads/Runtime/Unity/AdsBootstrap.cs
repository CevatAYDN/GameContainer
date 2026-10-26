using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

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
        private static Func<ProviderConfig, IConsentFlow> _sharedConsent;
        private static AdProviderKind _sharedConsentRunBy;

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

        private UnityAdTelemetry _telemetry;
        private float _timeScaleBeforeAd = 1f;
        private bool _audioWasPaused;
        private SimulatorOverlay _overlay;
        private bool _startRequested;

        public static AdsBootstrap Instance { get; private set; }

        public AdsSettings Settings => _settings;
        public BuildKind Build { get; private set; }
        public AdPlatform Platform { get; private set; }
        public GateResult Gate { get; private set; }
        /// <summary>What runs: the SDK, the labelled simulator, or nothing (<see cref="ProviderGate.Host"/>).</summary>
        public HostMode Mode { get; private set; }
        public IAdProvider Provider { get; private set; }
        public SimulatorProvider Simulator => Mode == HostMode.Simulator ? Provider as SimulatorProvider : null;
        public AdController Controller { get; private set; }
        public UnityAdTelemetry Telemetry => _telemetry;
        public string DeviceAdvertisingId { get; private set; }
        /// <summary>Raised once the controller exists (the advertising id is fetched off the main thread first).</summary>
        public event Action Started;

        /// <summary>Sets the settings and simulator scenario before the component is enabled (tests, code-made bootstraps).</summary>
        public void Configure(AdsSettings settings, SimulatorScenario simulatorScenario = SimulatorScenario.Normal, bool logEvents = true, bool startOnAwake = true)
        {
            if (Controller != null || _startRequested) throw new InvalidOperationException("Configure before the bootstrap starts.");
            _settings = settings;
            _simulatorScenario = simulatorScenario;
            _logEvents = logEvents;
            _startOnAwake = startOnAwake;
        }

        /// <summary>
        /// Starts the ad stack once: reads the advertising id, then builds the provider or the simulator and the controller,
        /// which runs the consent flow before the SDK. Awake calls it unless startOnAwake is off; a game that starts ads
        /// later (after its own explanation, never for a player who owns the ad-free product) calls it itself, after Awake.
        /// Later calls do nothing.
        /// </summary>
        public void StartAds()
        {
            if (_startRequested || Instance != this || _settings == null) return;
            _startRequested = true;
            var mainThread = new UnityMainThread();
            DeviceIdentity.FetchAdvertisingId(id => mainThread.Post(() => StartWith(id, mainThread)));
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
        /// A consent platform for the mediations that ship no form: com.nexus.ads.admob registers Google's User Messaging
        /// Platform here (issue #63). The provider of <paramref name="runByProvider"/> runs that platform itself and keeps its
        /// own flow. A null factory clears the registration.
        /// </summary>
        public static void RegisterSharedConsentFlow(Func<ProviderConfig, IConsentFlow> factory, AdProviderKind runByProvider)
        {
            _sharedConsent = factory;
            _sharedConsentRunBy = factory == null ? AdProviderKind.None : runByProvider;
        }

        /// <summary>
        /// The consent flow that runs before <paramref name="provider"/> initializes: the shared platform when one is
        /// registered and the selected mediation does not run it itself, otherwise the provider's own flow (LevelPlay's is a
        /// pass-through, since LevelPlay ships no form).
        /// </summary>
        public static IConsentFlow ConsentFlowFor(AdProviderKind selected, IAdProvider provider, ProviderConfig config)
            => _sharedConsent != null && selected != _sharedConsentRunBy ? _sharedConsent(config) : provider as IConsentFlow;

        public static BuildKind CurrentBuild => Application.isEditor ? BuildKind.Editor : Debug.isDebugBuild ? BuildKind.Development : BuildKind.Release;

        public static AdPlatform CurrentPlatform => Application.platform == RuntimePlatform.IPhonePlayer ? AdPlatform.Ios : AdPlatform.Android;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            if (_settings == null) _settings = AdsSettings.Load();
            if (_settings == null) { Debug.LogError("[Nexus Ads] No AdsSettings: create one with Nexus > Ads Settings or assign it on the bootstrap."); return; }
            Build = CurrentBuild;
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
            _telemetry = new UnityAdTelemetry(_logEvents && Build != BuildKind.Release);

            Factories.TryGetValue(_settings.SelectedMediation, out var factory);
            var decision = BuildOutlook.Decide(_settings, Build, config.Provider, advertisingId, factory != null);
            Gate = decision.Gate;
            Mode = decision.Mode;
            IConsentFlow consent;
            if (Mode == HostMode.Provider)
            {
                Provider = factory(scheduler, config.Provider);
                consent = ConsentFlowFor(_settings.SelectedMediation, Provider, config.Provider);
            }
            else
            {
                if (Mode == HostMode.Idle && !(Gate.Decision == GateDecision.Blocked && Gate.Rule == null)) // the rule-less block is the ad-free release
                    Debug.LogError("[Nexus Ads] no ads run: " + BuildOutlook.IdleReason(_settings, Gate));
                else if (Gate.Rule != null)
                    Debug.LogWarning("[Nexus Ads] " + Gate.Rule + ": " + Gate.Reason + "; the simulator runs (labelled, not a real ad)");
                // an idle host keeps a simulator that is never started, so every show answers NotReady
                var simulator = new SimulatorProvider(scheduler, _simulatorScenario) { Interactive = Mode == HostMode.Simulator };
                if (Mode == HostMode.Simulator) { _overlay = gameObject.AddComponent<SimulatorOverlay>(); _overlay.Bind(simulator); }
                Provider = simulator;
                consent = simulator;
            }

            Controller = new AdController(config, Provider, new UnityAdClock(), visibility, mainThread, scheduler, new PlayerPrefsPacingStore(), _telemetry, consent);
            Controller.AdOpened += OnAdOpened;
            Controller.AdClosed += OnAdClosed;
            // tools/device-acceptance counts "[Nexus Ads] PolicyViolation" lines; keep the token
            if (Build != BuildKind.Release) Controller.Violation += v => Debug.LogError("[Nexus Ads] PolicyViolation " + v);
            if (Mode != HostMode.Idle) Controller.Start(); // an idle host (ad-free, blocked, provider missing) builds the controller and never starts it
            if (Build != BuildKind.Release) gameObject.AddComponent<AdsDebugPanel>().Bind(this);
            // one line per start in every build, no ids: tools/device-acceptance -ExpectAds reads it (issue #52)
            Debug.Log("[Nexus Ads] started mode=" + Mode + " mediation=" + _settings.SelectedMediation + " gate=" + Gate.Decision
                + " rule=" + (Gate.Rule ?? "-") + " testMode=" + (config.Provider.TestMode ? "on" : "off"));
            Started?.Invoke();
        }

        private void OnAdOpened(AdFormat format)
        {
            if (_pauseAudioDuringAds) { _audioWasPaused = AudioListener.pause; AudioListener.pause = true; }
            if (_pauseTimeDuringAds) { _timeScaleBeforeAd = Time.timeScale; Time.timeScale = 0f; }
        }

        private void OnAdClosed(AdFormat format, AdResult result)
        {
            if (_pauseAudioDuringAds) AudioListener.pause = _audioWasPaused;
            if (_pauseTimeDuringAds) Time.timeScale = _timeScaleBeforeAd;
            if (_overlay != null) _overlay.enabled = false; // its OnGUI runs only while a show is open (issue #83)
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Controller?.Dispose();
            Simulator?.Cancel();
            Instance = null;
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
            new Thread(() =>
            {
                string id;
                AndroidJNI.AttachCurrentThread(); // JNI from a thread Unity did not attach aborts natively instead of throwing
                try
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var client = new AndroidJavaClass("com.google.android.gms.ads.identifier.AdvertisingIdClient"))
                    using (var info = client.CallStatic<AndroidJavaObject>("getAdvertisingIdInfo", activity))
                        id = info.Call<string>("getId");
                }
                catch (Exception ex)
                {
                    id = SystemInfo.deviceUniqueIdentifier;
                    Debug.LogWarning("[Nexus Ads] the advertising id is unavailable (" + ex.GetType().Name + ": " + ex.Message + "); the gate uses this device's SystemInfo.deviceUniqueIdentifier instead, which a mediation dashboard's test device list does not know");
                }
                finally { AndroidJNI.DetachCurrentThread(); }
                onFetched(id);
            }) { IsBackground = true, Name = "Nexus.Ads.AdvertisingId" }.Start();
#else
            onFetched(SystemInfo.deviceUniqueIdentifier);
#endif
        }
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
        {
            var controller = Controller;
            if (controller == null) { onResult?.Invoke(AdResult.NotReady); return; }
            controller.Show(placement, onResult);
        }

        public static void HideBanner() => Controller?.HideBanner();

        /// <summary>A paid reward no one acknowledged yet (the previous run may have been killed); pay it, then acknowledge.</summary>
        public static RewardReceipt? PendingReward => Controller?.PendingReward;

        public static void AcknowledgeReward(string receiptId) => Controller?.AcknowledgeReward(receiptId);

        public static bool PrivacyOptionsRequired => Controller != null && Controller.PrivacyOptionsRequired;

        public static void ShowPrivacyOptions(Action onClosed = null)
        {
            var controller = Controller;
            if (controller == null) { onClosed?.Invoke(); return; }
            controller.ShowPrivacyOptions(onClosed);
        }

        /// <summary>Subscribes now and again whenever the bootstrap (re)starts; returns an unsubscribe action.</summary>
        public static Action OnReadyChanged(Action handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var controller = Controller;
            if (controller != null) controller.ReadyChanged += handler;
            Action late = null;
            if (controller == null && AdsBootstrap.Instance != null)
            {
                // the start may already have filled an ad (the simulator fills at once), so the late subscriber reads readiness once
                late = () => { if (AdsBootstrap.Instance != null && AdsBootstrap.Instance.Controller != null) { AdsBootstrap.Instance.Controller.ReadyChanged += handler; handler(); } };
                AdsBootstrap.Instance.Started += late;
            }
            return () =>
            {
                var c = Controller;
                if (c != null) c.ReadyChanged -= handler;
                if (late != null && AdsBootstrap.Instance != null) AdsBootstrap.Instance.Started -= late;
            };
        }
    }
}
