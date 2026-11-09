#if UNITY_EDITOR && NEXUS_ADMOB && NEXUS_ADS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GoogleMobileAds;
using GoogleMobileAds.Api;
using GoogleMobileAds.Common;
using GoogleMobileAds.Ump.Api;
using GoogleMobileAds.Ump.Common;
using Nexus.Ads;
using Nexus.Ads.AdMob;
using NUnit.Framework;
using AdFormat = Nexus.Ads.AdFormat;

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsAdMobSdkTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FaultHook_ReceivedRewardSurvivesDestroyAndKeepsItsShow(bool duringNext)
        {
            using var sdk = new SdkScope();
            using var rig = new Rig(sdk);
            if (duringNext) rig.Provider.RewardFaults.DebugDeliverRewardDuringNextShow();
            else rig.Provider.RewardFaults.DebugDelayReward(TimeSpan.FromSeconds(3));
            AdResult? first = null, second = null;
            rig.Controller.Show("first", result => first = result);
            var original = sdk.Factory.Rewarded[0];
            original.RewardQueued();
            original.CloseQueued();
            Assert.That(first, Is.EqualTo(AdResult.Closed));
            Assert.That(original.DestroyCalls, Is.EqualTo(1));
            Assert.That(rig.Controller.PendingReward, Is.Null);
            rig.Controller.Show("second", result => second = result);
            if (!duringNext) Assert.That(rig.Scheduler.RunNext(), Is.True);
            Assert.That(rig.Controller.PendingReward?.Placement, Is.EqualTo("first"));
            Assert.That(second, Is.Null);
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            Assert.That(rig.Provider.RewardFaults.DebugReplayLastReward(), Is.True);
            Assert.That(rig.Controller.PendingReward, Is.Null);
        }

        [Test]
        public void FaultHook_DisposeCancelsHeldSdkReward()
        {
            using var sdk = new SdkScope();
            using var rig = new Rig(sdk);
            rig.Provider.RewardFaults.DebugDelayReward(TimeSpan.FromSeconds(3));
            rig.Controller.Show("first", _ => { });
            sdk.Factory.Rewarded[0].RewardQueued();
            sdk.Factory.Rewarded[0].CloseQueued();
            ((IDisposable)rig.Provider).Dispose();
            rig.Scheduler.RunNext();
            Assert.That(rig.Controller.PendingReward, Is.Null);
            Assert.That(rig.Provider.RewardFaults.DebugReplayLastReward(), Is.False);
        }

        [Test]
        public void A03_RewardQueuedAfterCloseKeepsTheOriginalShowReceipt()
        {
            using var sdk = new SdkScope();
            using var rig = new Rig(sdk);
            var results = new List<AdResult>();
            rig.Controller.Show("first", results.Add);
            var first = sdk.Factory.Rewarded[0];

            first.CloseQueued();
            first.RewardQueued();

            Assert.That(rig.Controller.PendingReward?.Placement, Is.EqualTo("first"),
                "A reward delivered after close must not disappear merely because the close callback arrived first.");
            Assert.That(results, Is.EqualTo(new[] { AdResult.Closed }),
                "The internal earned notification must not repeat the public completion.");
            Assert.That(first.DestroyCalls, Is.EqualTo(1), "Recovery must not require retaining the closed SDK ad.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            first.RewardQueued();
            first.CloseQueued();
            Assert.That(rig.Controller.PendingReward, Is.Null, "A duplicate must not recreate an acknowledged receipt.");
            Assert.That(results.Count, Is.EqualTo(1));
        }

        [Test]
        public void A03_CloseQueuedByAnExpiredAdDoesNotCloseTheReplacementShow()
        {
            using var sdk = new SdkScope();
            using var rig = new Rig(sdk);
            AdResult? firstResult = null;
            rig.Controller.Show("first", result => firstResult = result);
            var first = sdk.Factory.Rewarded[0];
            rig.Visibility.Set(false);
            rig.Visibility.Set(true);
            Assert.That(rig.Scheduler.RunNext(), Is.True);
            Assert.That(firstResult, Is.EqualTo(AdResult.Failed), "The controller's watchdog must end the unanswered first show.");
            Assert.That(rig.Controller.IsReady("second"), Is.True);
            AdResult? secondResult = null;
            rig.Controller.Show("second", result => secondResult = result);
            var second = sdk.Factory.Rewarded[1];

            first.CloseQueued();

            Assert.That(secondResult, Is.Null, "The old ad's queued close does not belong to the replacement.");
            Assert.That(second.DestroyCalls, Is.Zero, "A stale close must not destroy the currently showing ad.");
            first.RewardQueued();
            Assert.That(rig.Controller.PendingReward?.Placement, Is.EqualTo("first"),
                "A watchdog-retired ad's earned notification must retain its original placement.");
            Assert.That(secondResult, Is.Null);
            Assert.That(first.DestroyCalls, Is.EqualTo(1), "The old SDK handle must be released once.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            first.RewardQueued();
            Assert.That(rig.Controller.PendingReward, Is.Null);
        }

        [Test]
        public void A06_OwnerTeardownDestroysTheLoadedSdkAd()
        {
            using var sdk = new SdkScope();
            using var rig = new Rig(sdk);
            var loaded = sdk.Factory.Rewarded[0];
            rig.Controller.Dispose();
            IAdProvider provider = rig.Provider;

            (provider as IDisposable)?.Dispose();
            (provider as IDisposable)?.Dispose();
            provider.Load(AdFormat.Rewarded);

            Assert.That(loaded.DestroyCalls, Is.EqualTo(1),
                "The bootstrap's provider-disposal boundary must release the loaded SDK object.");
            Assert.That(sdk.Factory.Rewarded.Count, Is.EqualTo(1), "Disposal must not admit another SDK load.");
        }

        [Test]
        public void A06_LoadCompletionAfterOwnerTeardownCannotReviveTheProvider()
        {
            using var sdk = new SdkScope();
            sdk.Factory.AutoLoad = false;
            using var rig = new Rig(sdk);
            var pending = sdk.Factory.Rewarded[0];
            int loadedEvents = 0;
            rig.Provider.Loaded += _ => loadedEvents++;
            rig.Controller.Dispose();
            IAdProvider provider = rig.Provider;
            (provider as IDisposable)?.Dispose();

            pending.CompleteLoad();

            Assert.That(loadedEvents, Is.Zero, "A retired provider must not publish the stale load.");
            Assert.That(pending.DestroyCalls, Is.EqualTo(1), "The returned SDK object still needs to be released.");
        }

        [Test]
        public void A06_ConsentUpdateAfterFlowOwnerTeardownDoesNotRequestAForm()
        {
            using var sdk = new SdkScope();
            sdk.Consent.AllowRequests = false;
            var clock = new Clock();
            var scheduler = new Scheduler(clock);
            var provider = new SimulatorProvider(scheduler);
            var consent = new UmpConsentFlow(false, false);
            using var controller = new AdController(Config(), provider, clock, new Visibility(), new MainThread(),
                scheduler, new Store(), new Telemetry(), consent);
            controller.Start();
            Assert.That(controller.State, Is.EqualTo(AdController.Phase.AwaitingConsent));
            Assert.That(sdk.Consent.HasPendingUpdate, Is.True);

            controller.Dispose();
            IConsentFlow ownedFlow = consent;
            (ownedFlow as IDisposable)?.Dispose();
            sdk.Consent.CompleteUpdate();

            Assert.That(sdk.Forms.LoadAndShowCalls, Is.Zero,
                "The obsolete UMP update must not request a new form after the flow's owner released it.");
        }

        private static AdsConfig Config() => new AdsConfig(
            new[] { new AdPlacement("first", AdFormat.Rewarded), new AdPlacement("second", AdFormat.Rewarded) },
            AdPolicy.Default, new ProviderConfig("fixture-app", new Dictionary<AdFormat, string> { { AdFormat.Rewarded, "fixture-unit" } },
                false, null, false, false, false, "G"), BuildKind.Editor);

        private sealed class Rig : IDisposable
        {
            public readonly AdMobProvider Provider;
            public readonly AdController Controller;
            public readonly Visibility Visibility = new Visibility();
            public readonly Scheduler Scheduler;

            public Rig(SdkScope sdk)
            {
                Assert.That(MediationLock.Holder, Is.Null, "Run this fixture with no other mediation controller alive.");
                var clock = new Clock();
                Scheduler = new Scheduler(clock);
                var config = Config();
                Provider = new AdMobProvider(config.Provider, Scheduler);
                Controller = new AdController(config, Provider, clock, Visibility, new MainThread(),
                    Scheduler, new Store(), new Telemetry());
                Controller.Start();
                Assert.That(Controller.State, Is.EqualTo(AdController.Phase.Running));
            }

            public void Dispose()
            {
                Controller.Dispose();
                IAdProvider provider = Provider;
                (provider as IDisposable)?.Dispose();
            }
        }

        // These private factory hooks and interfaces were read from the locked 11.6.0 DLLs.
        // A different binary is inconclusive, never permission to fall through to a real SDK client.
        private sealed class SdkScope : IDisposable
        {
            private readonly FieldInfo _factoryField;
            private readonly FieldInfo _instanceField;
            private readonly FieldInfo _consentField;
            private readonly FieldInfo _dispatchField;
            private readonly object _oldFactory, _oldInstance, _oldConsent;
            private readonly bool _oldDispatch;
            public readonly Factory Factory = new Factory();
            public readonly ConsentClient Consent = new ConsentClient();
            public readonly FormClient Forms = new FormClient();

            public SdkScope()
            {
                Identity(typeof(MobileAds).Assembly, "3a97825f-f59b-47e3-a638-991ba60a3143");
                Identity(typeof(IClientFactory).Assembly, "5254d49d-89df-40e3-a057-3114de63ba8e");
                Identity(typeof(ConsentInformation).Assembly, "e2097b6a-fdc0-4f51-88e9-5f36e3765600");
                _factoryField = StaticField(typeof(MobileAds), "clientFactory");
                _instanceField = StaticField(typeof(MobileAds), "instance");
                _consentField = StaticField(typeof(ConsentInformation), "_clientFactory");
                _dispatchField = StaticField(typeof(MobileAds), "<RaiseAdEventsOnUnityMainThread>k__BackingField");
                _oldFactory = _factoryField.GetValue(null);
                _oldInstance = _instanceField.GetValue(null);
                _oldConsent = _consentField.GetValue(null);
                _oldDispatch = (bool)_dispatchField.GetValue(null);
                _factoryField.SetValue(null, Factory);
                _instanceField.SetValue(null, new MobileAds());
                _consentField.SetValue(null, new ConsentFactory(Consent, Forms));
                _dispatchField.SetValue(null, false);
            }

            public void Dispose()
            {
                _instanceField.SetValue(null, _oldInstance);
                _factoryField.SetValue(null, _oldFactory);
                _consentField.SetValue(null, _oldConsent);
                _dispatchField.SetValue(null, _oldDispatch);
            }

            private static FieldInfo StaticField(Type type, string name)
                => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("The pinned SDK factory hook is missing: " + name);

            private static void Identity(Assembly assembly, string moduleId)
            {
                if (assembly.ManifestModule.ModuleVersionId != new Guid(moduleId))
                    Assert.Inconclusive("This offline fixture requires the locked Google Mobile Ads 11.6.0 binary: " + assembly.GetName().Name);
            }
        }

        private sealed class Factory : IClientFactory
        {
            public bool AutoLoad = true;
            public readonly List<RewardedClient> Rewarded = new List<RewardedClient>();
            private readonly MobileClient _mobile = new MobileClient();
            public IRewardedAdClient BuildRewardedAdClient()
            {
                var client = new RewardedClient(AutoLoad);
                Rewarded.Add(client);
                return client;
            }
            public IMobileAdsClient MobileAdsInstance() => _mobile;
            public IAppStateEventClient BuildAppStateEventClient() => throw Unexpected();
            public IAppOpenAdClient BuildAppOpenAdClient() => throw Unexpected();
            public IBannerClient BuildBannerClient() => throw Unexpected();
            public IAdManagerBannerClient BuildAdManagerBannerClient() => throw Unexpected();
            public IInterstitialClient BuildInterstitialClient() => throw Unexpected();
            public IAdManagerInterstitialClient BuildAdManagerInterstitialClient() => throw Unexpected();
            public IRewardedInterstitialAdClient BuildRewardedInterstitialAdClient() => throw Unexpected();
            public INativeOverlayAdClient BuildNativeOverlayAdClient() => throw Unexpected();
            public IPictureInPictureAdClient BuildPictureInPictureAdClient() => throw Unexpected();
            public IApplicationPreferencesClient ApplicationPreferencesInstance() => throw Unexpected();
            public IAppOpenAdPreloaderClient BuildAppOpenAdPreloaderClient() => throw Unexpected();
            public IInterstitialAdPreloaderClient BuildInterstitialAdPreloaderClient() => throw Unexpected();
            public IRewardedAdPreloaderClient BuildRewardedAdPreloaderClient() => throw Unexpected();
            private static Exception Unexpected() => new InvalidOperationException("An unexpected SDK client was requested; no real client is allowed.");
        }

        private sealed class MobileClient : IMobileAdsClient, IInitializationStatusClient
        {
            private RequestConfiguration _configuration;
            public void Initialize(Action<IInitializationStatusClient> completed) => completed(this);
            public void SetRequestConfiguration(RequestConfiguration configuration) => _configuration = configuration;
            public RequestConfiguration GetRequestConfiguration() => _configuration;
            public AdapterStatus getAdapterStatusForClassName(string name) => null;
            public Dictionary<string, AdapterStatus> getAdapterStatusMap() => new Dictionary<string, AdapterStatus>();
            public void Preload(List<PreloadConfiguration> configurations, Action<PreloadConfiguration> loaded, Action<PreloadConfiguration> failed) => throw new NotSupportedException();
            public void DisableMediationInitialization() { }
            public void SetApplicationVolume(float volume) { }
            public void SetApplicationMuted(bool muted) { }
            public void SetiOSAppPauseOnBackground(bool paused) { }
            public void DisableSDKCrashReporting() { }
            public float GetDeviceScale() => 1;
            public int GetDeviceSafeWidth() => 320;
            public Version GetSDKVersion() => new Version(11, 6, 0);
            public void OpenAdInspector(Action<AdInspectorErrorClientEventArgs> completed) => throw new NotSupportedException();
        }

        private sealed class RewardedClient : IRewardedAdClient
        {
            private readonly bool _autoLoad;
            private string _unit;
            private Action _queuedClose;
            private Action<Reward> _queuedReward;
            public int DestroyCalls;
            public long PlacementId { get; set; }
            public event Action OnAdLoaded;
            public event Action OnAdDidPresentFullScreenContent;
            public event Action OnAdDidDismissFullScreenContent;
            public event Action<Reward> OnUserEarnedReward;
            public event Action<LoadAdErrorClientEventArgs> OnAdFailedToLoad { add { } remove { } }
            public event Action<AdValue> OnPaidEvent { add { } remove { } }
            public event Action<AdErrorClientEventArgs> OnAdFailedToPresentFullScreenContent { add { } remove { } }
            public event Action OnAdDidRecordImpression { add { } remove { } }
            public event Action OnAdClicked { add { } remove { } }
            public RewardedClient(bool autoLoad) => _autoLoad = autoLoad;
            public void CreateRewardedAd() { }
            public void LoadAd(string unit, AdRequest request) { _unit = unit; if (_autoLoad) CompleteLoad(); }
            public void CompleteLoad() => OnAdLoaded?.Invoke();
            public void Show()
            {
                _queuedClose = OnAdDidDismissFullScreenContent;
                _queuedReward = OnUserEarnedReward;
                OnAdDidPresentFullScreenContent?.Invoke();
            }
            public void CloseQueued() => _queuedClose?.Invoke();
            public void RewardQueued() => _queuedReward?.Invoke(new Reward());
            public void DestroyRewardedAd() => DestroyCalls++;
            public string GetAdUnitID() => _unit;
            public Reward GetRewardItem() => new Reward();
            public IResponseInfoClient GetResponseInfoClient() => null;
            public void SetServerSideVerificationOptions(ServerSideVerificationOptions options) { }
            public bool IsAdAvailable(string unit) => false;
            public IRewardedAdClient PollAd(string unit) => null;
        }

        private sealed class ConsentFactory : IUmpClientFactory
        {
            private readonly ConsentClient _consent;
            private readonly FormClient _forms;
            public ConsentFactory(ConsentClient consent, FormClient forms) { _consent = consent; _forms = forms; }
            public IConsentInformationClient ConsentInformationClient() => _consent;
            public IConsentFormClient ConsentFormClient() => _forms;
        }

        private sealed class ConsentClient : IConsentInformationClient
        {
            public bool AllowRequests = true;
            private Action _complete;
            public bool HasPendingUpdate => _complete != null;
            public void Update(ConsentRequestParameters parameters, Action completed, Action<FormError> failed) => _complete = completed;
            public void CompleteUpdate() { var completed = _complete; _complete = null; completed?.Invoke(); }
            public bool CanRequestAds() => AllowRequests;
            public int GetConsentStatus() => (int)(AllowRequests ? ConsentStatus.Obtained : ConsentStatus.Required);
            public int GetPrivacyOptionsRequirementStatus() => (int)PrivacyOptionsRequirementStatus.Required;
            public bool IsConsentFormAvailable() => true;
            public void Reset() => throw new NotSupportedException("The fixture must not reset real or simulated consent.");
        }

        private sealed class FormClient : IConsentFormClient
        {
            public int LoadAndShowCalls;
            public void LoadAndShowConsentFormIfRequired(Action<FormError> completed) { LoadAndShowCalls++; completed(null); }
            public void ShowPrivacyOptionsForm(Action<FormError> completed) => throw new NotSupportedException();
            public void Load(Action loaded, Action<FormError> failed) => throw new NotSupportedException();
            public void Show(Action<FormError> completed) => throw new NotSupportedException();
        }

        private sealed class Clock : IAdClock
        {
            public double Seconds { get; set; } = 1000;
            public long UtcDay => 100;
            public double UtcSeconds => 8640000 + Seconds;
        }

        private sealed class Visibility : IAppVisibility
        {
            public bool Visible { get; private set; } = true;
            public event Action<bool> Changed;
            public void Set(bool visible) { Visible = visible; Changed?.Invoke(visible); }
        }

        private sealed class MainThread : IMainThread
        {
            public bool IsCurrent => true;
            public void Post(Action action) => action();
        }

        private sealed class Scheduler : IAdScheduler
        {
            private readonly Clock _clock;
            private readonly List<Entry> _pending = new List<Entry>();
            public Scheduler(Clock clock) => _clock = clock;
            public IDisposable Schedule(double seconds, Action action)
            {
                var entry = new Entry { At = _clock.Seconds + seconds, Action = action };
                _pending.Add(entry);
                return entry;
            }
            public bool RunNext()
            {
                var entry = _pending.Where(item => !item.Done).OrderBy(item => item.At).FirstOrDefault();
                if (entry == null) return false;
                entry.Done = true;
                _clock.Seconds = entry.At;
                entry.Action();
                return true;
            }
            private sealed class Entry : IDisposable
            {
                public double At;
                public Action Action;
                public bool Done;
                public void Dispose() => Done = true;
            }
        }

        private sealed class Store : IPacingStore
        {
            private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();
            public string Get(string key) => _rows.TryGetValue(key, out string value) ? value : null;
            public void Set(string key, string value) => _rows[key] = value;
        }

        private sealed class Telemetry : IAdTelemetry { public void Record(in AdEvent e) { } }
    }
}
#endif
