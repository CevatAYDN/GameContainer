#if NEXUS_LEVELPLAY
// #412 requires compile-time exclusion from release players, not only a runtime debug check.
#pragma warning disable UAC0009
using System;
using System.Collections.Generic;
using Nexus.Ads.Unity;
using Unity.Services.LevelPlay;
using UnityEngine;

// Nothing references a provider assembly; it registers itself from a RuntimeInitializeOnLoadMethod. Without this
// attribute the IL2CPP linker drops the whole assembly and the provider never registers (seen on a device, 2026-10-24, when the player then ran the simulator; since issue #19 a store build runs nothing).
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("com.nexus.ads.levelplay.tests")]

namespace Nexus.Ads.LevelPlay
{
    /// <summary>
    /// Unity LevelPlay (com.unity.services.levelplay 9.x) as the core's <see cref="IAdProvider"/>: one
    /// loaded <see cref="LevelPlayRewardedAd"/> and <see cref="LevelPlayInterstitialAd"/> per configured ad
    /// unit, replaced after consumption, a banner on demand, impression data as <see cref="AdRevenue"/>. The SDK fires
    /// <c>OnAdRewarded</c> after <c>OnAdClosed</c> at times (its own documentation says so), so a close
    /// waits <see cref="RewardGraceSeconds"/> for a late reward before it answers Closed. Test mode
    /// (the Test tab) enables the integration test suite metadata and adapter debug logging before
    /// Init; test ads themselves come from the devices listed in the LevelPlay dashboard.
    /// Consent: LevelPlay ships no form. GDPR consent collected by Google UMP or a compatible CMP is
    /// shared with the SDK automatically (SDK 7.7.0+); a game with another CMP calls
    /// <see cref="SetGdprConsent"/>. COPPA goes to <c>LevelPlayPrivacySettings.SetCOPPA</c> from the
    /// settings' child-directed flag before Init; a child-directed app also opts out of the advertising id
    /// and starts only the Families self-certified networks (issue #194). Init waits for a network: offline,
    /// the SDK answers Init from its cached response and every load of the session ends in 509 (issue #125).
    /// </summary>
    public sealed class LevelPlayProvider : IAdProvider, IConsentFlow, IRewardedShowProvider, IDisposable
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        , IRewardFaultDebug
#endif
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private RewardFaultInjection _rewardFaults;
        public RewardFaultInjection RewardFaults => _rewardFaults ??= new RewardFaultInjection(ProviderName, _scheduler);
#endif
        public const string ProviderName = "levelplay";
        public const double RewardGraceSeconds = 1.5;
        public const double NetworkPollSeconds = 2;

        private readonly IAdScheduler _scheduler;
        private readonly Func<bool> _networkReachable;
        private readonly Action<string, string> _setMetaData;
        private readonly UnityMainThread _main = new UnityMainThread();
        private IDisposable _networkWait;
        private readonly Dictionary<AdFormat, string> _units = new Dictionary<AdFormat, string>();
        private LevelPlayRewardedAd _rewarded;
        private LevelPlayInterstitialAd _interstitial;
        private AdState _rewardedState, _interstitialState;
        private LevelPlayBannerAd _banner;
        private string _bannerPlacement;
        private BannerPosition _bannerPosition;
        private bool _testTab, _bannerUnitMissingWarned;
        private Action<bool> _onReady;
        private bool _initialized, _disposed;
        private ShowState _show;

        private sealed class AdState
        {
            public readonly AdFormat Format;
            public ShowState Show;
            private Action _destroy;
            public bool Released { get; private set; }

            public AdState(AdFormat format, Action destroy) { Format = format; _destroy = destroy; }
            public void Release()
            {
                if (Released) return;
                Released = true;
                var destroy = _destroy;
                _destroy = null;
                destroy?.Invoke();
            }
        }

        private sealed class ShowState
        {
            public readonly AdState Ad;
            public readonly string Placement;
            public readonly Action<AdResult> OnResult;
            public readonly Action OnRewardEarned;
            public bool Rewarded, Closed, Done;
            public IDisposable Grace;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public RewardFaultInjection.Show Fault;
#endif

            public ShowState(AdState ad, string placement, Action<AdResult> result, Action earned)
            {
                Ad = ad;
                Placement = placement ?? "";
                OnResult = result;
                OnRewardEarned = earned;
            }
        }

        public LevelPlayProvider(IAdScheduler scheduler)
            : this(scheduler, () => Application.internetReachability != NetworkReachability.NotReachable) { }

        // setMetaData: the editor SDK drops metadata, so the tests read what the provider sends here
        internal LevelPlayProvider(IAdScheduler scheduler, Func<bool> networkReachable, Action<string, string> setMetaData = null)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _networkReachable = networkReachable ?? throw new ArgumentNullException(nameof(networkReachable));
            _setMetaData = setMetaData ?? ((key, value) => global::Unity.Services.LevelPlay.LevelPlay.SetMetaData(key, value));
        }

        // ----- IAdProvider

        public string Name => ProviderName;

        public event Action<AdFormat> Loaded;
        public event Action<AdFormat, string> LoadFailed;
        public event Action<AdFormat> Opened;
        public event Action<AdRevenue> Revenue;

        public void Initialize(ProviderConfig config, Action<bool> onReady)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_disposed) throw new ObjectDisposedException(nameof(LevelPlayProvider));
            if (_initialized) { onReady?.Invoke(true); return; }
            _onReady = onReady;
            _testTab = config.TestMode; // LevelPlay's test mode is the Test tab (AdsSettings.ProviderConfigFor)
            _units.Clear();
            foreach (var pair in config.AdUnitIds) _units[pair.Key] = pair.Value;

            // privacy and test settings go in before Init, as the SDK's regulation page asks
            LevelPlayPrivacySettings.SetCOPPA(config.ChildDirected || config.UnderAgeOfConsent);
            if (config.ChildDirected)
            {
                // Play Families as LevelPlay's regulation page gives it (read 2026-11-01): no advertising id, and only the networks on
                // Google's Families self-certified list start (issue #194); the validator asks the manifest to remove AD_ID
                _setMetaData("is_deviceid_optout", "true");
                _setMetaData("Google_Family_Self_Certified_SDKS", "true");
            }
            if (config.TestMode)
            {
                _setMetaData("is_test_suite", "enable");
                global::Unity.Services.LevelPlay.LevelPlay.SetAdaptersDebug(true);
            }
            _networkWait?.Dispose();
            if (!_networkReachable()) Debug.Log("[Nexus Ads] LevelPlay init waits for a network");
            InitWhenReachable(config.AppKey);
        }

        // Offline, LevelPlay answers Init from its cached response and every later load ends in 509, the network back or not,
        // until the app restarts (device runs 2026-10-29 and -30, issue #125), so Init waits for a network.
        // shortcut: reachability means a network, not the internet; a captive portal or a SIM without data still takes the cached path. Upgrade when a game sees it.
        private void InitWhenReachable(string appKey)
        {
            _networkWait = null;
            if (_disposed) return;
            if (!_networkReachable()) { _networkWait = _scheduler.Schedule(NetworkPollSeconds, () => InitWhenReachable(appKey)); return; }
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess += OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed += OnInitFailed;
            global::Unity.Services.LevelPlay.LevelPlay.Init(appKey); // the editor platform answers only on an Android or iOS build target; elsewhere the SDK is absent and nothing fires
        }

        private void OnInitSuccess(LevelPlayConfiguration configuration) => OnMain(() =>
        {
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess -= OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed -= OnInitFailed;
            if (_initialized) return;
            _initialized = true;
            if (_units.TryGetValue(AdFormat.Rewarded, out var rewardedUnit) && !string.IsNullOrEmpty(rewardedUnit))
                CreateRewarded(rewardedUnit);
            if (_units.TryGetValue(AdFormat.Interstitial, out var interstitialUnit) && !string.IsNullOrEmpty(interstitialUnit))
                CreateInterstitial(interstitialUnit);
            var callback = _onReady; _onReady = null;
            callback?.Invoke(true);
        });

        private void CreateRewarded(string unit)
        {
            var ad = new LevelPlayRewardedAd(unit);
            var state = new AdState(AdFormat.Rewarded, ad.DestroyAd);
            _rewarded = ad;
            _rewardedState = state;
            ad.OnAdLoaded += _ => OnMain(() => { if (!state.Released) Loaded?.Invoke(state.Format); });
            ad.OnAdLoadFailed += error => OnMain(() => { if (!state.Released) LoadFailed?.Invoke(state.Format, Describe(error)); });
            ad.OnAdDisplayed += _ => OnMain(() => OnOpened(state));
            ad.OnAdDisplayFailed += (_, error) => OnMain(() => ShowFailed(state, error));
            ad.OnAdRewarded += (_, __) => OnMain(() => ReceiveReward(state.Show));
            ad.OnAdClosed += _ => OnMain(() => OnClosed(state.Show));
            ad.OnAdImpressionDataReady += data => OnMain(() => OnImpression(state.Format, data, state.Show?.Placement));
        }

        private void CreateInterstitial(string unit)
        {
            var ad = new LevelPlayInterstitialAd(unit);
            var state = new AdState(AdFormat.Interstitial, ad.DestroyAd);
            _interstitial = ad;
            _interstitialState = state;
            ad.OnAdLoaded += _ => OnMain(() => { if (!state.Released) Loaded?.Invoke(state.Format); });
            ad.OnAdLoadFailed += error => OnMain(() => { if (!state.Released) LoadFailed?.Invoke(state.Format, Describe(error)); });
            ad.OnAdDisplayed += _ => OnMain(() => OnOpened(state));
            ad.OnAdDisplayFailed += (_, error) => OnMain(() => ShowFailed(state, error));
            ad.OnAdClosed += _ => OnMain(() => OnClosed(state.Show));
            ad.OnAdImpressionDataReady += data => OnMain(() => OnImpression(state.Format, data, state.Show?.Placement));
        }

        private void OnInitFailed(LevelPlayInitError error) => OnMain(() =>
        {
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess -= OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed -= OnInitFailed;
            if (_initialized) return;
            Debug.LogWarning("[Nexus Ads] LevelPlay init failed: " + error);
            var callback = _onReady; _onReady = null;
            callback?.Invoke(false);
        });

        public void Load(AdFormat format)
        {
            if (_disposed) return;
            if (!_initialized) { LoadFailed?.Invoke(format, "SDK not initialized"); return; }
            if (!_units.TryGetValue(format, out var unit) || string.IsNullOrEmpty(unit))
            {
                LoadFailed?.Invoke(format, "no ad unit id for " + format);
                return;
            }
            switch (format)
            {
                case AdFormat.Rewarded:
                    if (_rewarded == null || _rewardedState.Show != null)
                    {
                        Retire(_rewardedState);
                        if (_disposed) return;
                        CreateRewarded(unit);
                    }
                    _rewarded.LoadAd();
                    return;
                case AdFormat.Interstitial:
                    if (_interstitial == null || _interstitialState.Show != null)
                    {
                        Retire(_interstitialState);
                        if (_disposed) return;
                        CreateInterstitial(unit);
                    }
                    _interstitial.LoadAd();
                    return;
                default: LoadFailed?.Invoke(format, _initialized ? "no ad unit id for " + format : "SDK not initialized"); return;
            }
        }

        public bool IsReady(AdFormat format)
        {
            if (_disposed) return false;
            switch (format)
            {
                case AdFormat.Rewarded: return _rewarded != null && _rewardedState.Show == null && _rewarded.IsAdReady();
                case AdFormat.Interstitial: return _interstitial != null && _interstitialState.Show == null && _interstitial.IsAdReady();
                default: return false;
            }
        }

        public void Show(AdFormat format, string placement, Action<AdResult> onResult)
            => Show(format, placement, onResult, null);

        void IRewardedShowProvider.ShowRewarded(string placement, Action<AdResult> onResult, Action onRewardEarned)
            => Show(AdFormat.Rewarded, placement, onResult, onRewardEarned);

        private void Show(AdFormat format, string placement, Action<AdResult> onResult, Action onRewardEarned)
        {
            if (onResult == null) throw new ArgumentNullException(nameof(onResult));
            if (!IsReady(format)) { onResult(AdResult.NotReady); return; }
            if (_show != null && !_show.Done) Retire(_show.Ad);
            var ad = format == AdFormat.Rewarded ? _rewardedState : _interstitialState;
            var show = _show = new ShowState(ad, placement, onResult, onRewardEarned);
            ad.Show = show;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (format == AdFormat.Rewarded)
                show.Fault = RewardFaults.BeginShow(placement, () => OnRewarded(show), () => ad.Released);
#endif
            try { if (format == AdFormat.Rewarded) _rewarded.ShowAd(placement); else _interstitial.ShowAd(placement); }
            catch { Retire(ad); throw; }
        }

        private void OnOpened(AdState ad)
        {
            if (!ad.Released && ad.Show != null && !ad.Show.Done && ReferenceEquals(_show, ad.Show))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (ad.Format == AdFormat.Rewarded) _rewardFaults?.Opened(ad.Show.Fault);
#endif
                Opened?.Invoke(ad.Format);
            }
        }

        private void ReceiveReward(ShowState show)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (show != null) RewardFaults.Receive(show.Fault);
#else
            OnRewarded(show);
#endif
        }

        private void OnRewarded(ShowState show)
        {
            if (show == null || show.Rewarded) return;
            show.Rewarded = true;
            show.OnRewardEarned?.Invoke();
            if (show.Closed) Finish(show, AdResult.Rewarded);
        }

        private void OnClosed(ShowState show)
        {
            if (show == null || show.Done || show.Closed) return;
            show.Closed = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _rewardFaults?.Closed(show.Fault);
#endif
            if (show.Ad.Format != AdFormat.Rewarded) { Finish(show, AdResult.Closed); return; }
            if (show.Rewarded) { Finish(show, AdResult.Rewarded); return; }
            show.Grace = _scheduler.Schedule(RewardGraceSeconds, () => Finish(show, show.Rewarded ? AdResult.Rewarded : AdResult.Closed));
        }

        // the show result carries no reason: the SDK's is written here, in every build, as the AdMob provider does (issue #164)
        private void ShowFailed(AdState ad, LevelPlayAdError error)
        {
            if (ad.Show == null || ad.Show.Done) return;
            Debug.LogWarning("[Nexus Ads] LevelPlay " + ad.Format + " failed to show: " + Describe(error));
            Finish(ad.Show, AdResult.Failed);
        }

        private void Finish(ShowState show, AdResult result)
        {
            if (_disposed || show == null || show.Done) return;
            show.Done = true;
            var grace = show.Grace;
            show.Grace = null;
            if (ReferenceEquals(_show, show)) _show = null;
            try { grace?.Dispose(); }
            finally
            {
                try { Release(show.Ad); }
                finally { show.OnResult(result); }
            }
        }

        private void Release(AdState ad)
        {
            if (ad == null) return;
            if (ReferenceEquals(_rewardedState, ad)) { _rewardedState = null; _rewarded = null; }
            if (ReferenceEquals(_interstitialState, ad)) { _interstitialState = null; _interstitial = null; }
            // Queued callbacks may retain this show's identity, but the provider keeps no history of SDK objects.
            // Delivery of additional native callbacks after handle destruction is not guaranteed here.
            ad.Release();
        }

        private void Retire(AdState ad)
        {
            var show = ad?.Show;
            if (show != null)
            {
                show.Done = true;
                if (ReferenceEquals(_show, show)) _show = null;
                var grace = show.Grace;
                show.Grace = null;
                try { grace?.Dispose(); }
                finally { Release(ad); }
            }
            else Release(ad);
        }

        public void ShowBanner(string placement, BannerPosition position)
        {
            if (_disposed || !_initialized) return;
            if (!_units.TryGetValue(AdFormat.Banner, out var unit) || string.IsNullOrEmpty(unit))
            {
                if (!_bannerUnitMissingWarned)
                    Debug.LogWarning("[Nexus Ads] LevelPlay has no banner ad unit id on the " + (_testTab ? "Test" : "Release") + " tab of the ads settings: ShowBanner shows nothing");
                _bannerUnitMissingWarned = true;
                return;
            }
            placement = placement ?? "";
            if (_banner != null && (position != _bannerPosition || placement != _bannerPlacement))
            {
                var previous = _banner;
                _banner = null;
                previous.DestroyAd(); // LevelPlay 9.6 fixes position and placement when the banner is built.
            }
            if (_banner == null)
            {
                _bannerPlacement = placement;
                _bannerPosition = position;
                var config = new LevelPlayBannerAd.Config.Builder()
                    .SetSize(LevelPlayAdSize.CreateAdaptiveAdSize())
                    .SetPosition(position == BannerPosition.Top ? LevelPlayBannerPosition.TopCenter : LevelPlayBannerPosition.BottomCenter)
                    .SetPlacementName(placement)
                    .SetDisplayOnLoad(true)
                    .Build();
                var banner = _banner = new LevelPlayBannerAd(unit, config);
                banner.OnAdLoadFailed += error => OnMain(() =>
                {
                    if (ReferenceEquals(_banner, banner)) Debug.LogWarning("[Nexus Ads] LevelPlay banner did not load: " + Describe(error));
                });
                banner.OnAdImpressionDataReady += data => OnMain(() =>
                {
                    if (ReferenceEquals(_banner, banner)) OnImpression(AdFormat.Banner, data, placement);
                });
                banner.LoadAd();
                return;
            }
            _banner.ShowAd();
        }

        public void HideBanner() => _banner?.HideAd();

        public void OpenTestSuite() { if (!_disposed) global::Unity.Services.LevelPlay.LevelPlay.LaunchTestSuite(); }

        // the format comes from the ad object, not data.AdFormat: the device SDK sends "rewarded_video", the editor SDK "editor_mock_format"
        private void OnImpression(AdFormat format, LevelPlayImpressionData data, string placement = null)
        {
            if (data == null) return;
            Revenue?.Invoke(new AdRevenue(data.AdNetwork ?? ProviderName, placement ?? data.Placement ?? "", format, data.Revenue ?? 0, "USD", data.Precision ?? ""));
        }

        private static string Describe(LevelPlayAdError error) => error == null ? "unknown" : error.ErrorCode + " " + error.ErrorMessage;

        /// <summary>
        /// Leaves LevelPlay's init events, cancels waits and releases every owned ad instance once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _rewardFaults?.Dispose();
#endif
            _initialized = false;
            var wait = _networkWait;
            _networkWait = null;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess -= OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed -= OnInitFailed;
            _onReady = null;
            try { wait?.Dispose(); }
            finally
            {
                try { Retire(_rewardedState); }
                finally
                {
                    try { Retire(_interstitialState); }
                    finally
                    {
                        var banner = _banner;
                        _banner = null;
                        _show = null;
                        Loaded = null;
                        LoadFailed = null;
                        Opened = null;
                        Revenue = null;
                        banner?.DestroyAd();
                    }
                }
            }
        }

        private void OnMain(Action action)
        {
            if (_main.IsCurrent) { if (!_disposed) action(); }
            else _main.Post(() => { if (!_disposed) action(); });
        }

        // ----- IConsentFlow: LevelPlay ships no form; consent arrives from the game's CMP (Google UMP is shared automatically).

        public bool Completed => !_disposed;
        public bool CanRequestAds => !_disposed;
        public bool PrivacyOptionsRequired => false;
        public event Action Changed { add { } remove { } }
        public void Request(Action onCompleted) { if (!_disposed) onCompleted?.Invoke(); }
        public void ShowPrivacyOptions(Action onClosed) { if (!_disposed) onClosed?.Invoke(); }

        /// <summary>Forwards a GDPR answer from a CMP that is not shared with the SDK automatically (SDK 9.5.0+).</summary>
        public static void SetGdprConsent(bool consent) => LevelPlayPrivacySettings.SetGDPRConsent(consent);

        /// <summary>Forwards a CCPA "do not sell" opt-out.</summary>
        public static void SetDoNotSell(bool optedOut) => LevelPlayPrivacySettings.SetCCPA(optedOut);
    }

    /// <summary>Registers the provider with the bootstrap before any scene loads; only the selected mediation is ever created.</summary>
    public static class LevelPlayRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => AdsBootstrap.RegisterProvider(AdProviderKind.LevelPlay, (scheduler, _) => new LevelPlayProvider(scheduler));
    }
}
#endif
