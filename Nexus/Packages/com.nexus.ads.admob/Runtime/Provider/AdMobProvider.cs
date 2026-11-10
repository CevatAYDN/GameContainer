// #412 requires compile-time exclusion from release players, not only a runtime debug check.
using System;
using System.Collections.Generic;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using Nexus.Ads.Unity;
using UnityEngine;

// Nothing references a provider assembly; it registers itself from a RuntimeInitializeOnLoadMethod. Without this
// attribute the IL2CPP linker drops the whole assembly and the provider never registers (seen on a device, 2026-10-24, when the player then ran the simulator; since issue #19 a store build runs nothing).
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Google AdMob (Google Mobile Ads Unity plugin 11.6.0+) as the core's
    /// <see cref="IAdProvider"/>, with Google's User Messaging Platform (<see cref="UmpConsentFlow"/>) as its
    /// <see cref="IConsentFlow"/>; the package also registers that flow for the other mediations. The age treatment (child-directed or under the age of
    /// consent: <c>AgeRestrictedTreatment.Child</c>, which replaced the deprecated TFCD and TFUA tags in 11.3.0)
    /// and the content rating reach <c>RequestConfiguration</c> before <c>MobileAds.Initialize</c>. Every SDK
    /// callback (the UMP listeners arrive on the Android UI thread) continues on the main thread.
    /// Google has no test-mode flag: development builds reach the SDK only with Google's sample units
    /// (<see cref="ProviderGate"/>), and <see cref="SetTestDevices"/> registers Google-format test device
    /// ids for those builds only. The plugin reads the App ID from its own settings asset; the validator
    /// keeps that asset equal to the settings tab.
    /// </summary>
    public sealed class AdMobProvider : IAdProvider, IConsentFlow, IRewardedShowProvider, IDisposable
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
        , IRewardFaultDebug
#endif
    {
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
        private RewardFaultInjection _rewardFaults;
        public RewardFaultInjection RewardFaults => _rewardFaults ??= new RewardFaultInjection(ProviderName);
        public AdMobProvider(ProviderConfig config, IAdScheduler scheduler) : this(config)
            => _rewardFaults = new RewardFaultInjection(ProviderName, scheduler);
#endif
        public const string ProviderName = "admob";

        /// <summary>
        /// Google-format test device ids: the 32-character id the SDK prints in logcat ("setTestDeviceIds")
        /// or in the Xcode console ("testDeviceIdentifiers"); on Android UMP prints the same id for its debug
        /// settings, on iOS add the id UMP prints as well. <paramref name="eeaConsentGeography"/> makes those
        /// devices see the consent form as in the EEA. Applied only while the configuration runs on Google's
        /// sample units, so a release build never carries them. Call before the bootstrap starts.
        /// </summary>
        public static void SetTestDevices(IEnumerable<string> googleTestDeviceIds, bool eeaConsentGeography = false)
            => UmpConsentFlow.SetDebugDevices(googleTestDeviceIds, eeaConsentGeography);

        private readonly ProviderConfig _config;
        private readonly UnityMainThread _main = new UnityMainThread();
        private RewardedAd _rewarded;
        private InterstitialAd _interstitial;
        private AdState _rewardedState, _interstitialState;
        private BannerView _banner;
        private string _bannerPlacement = "";
        private readonly UmpConsentFlow _consent;
        private bool _sdkStarted, _loadingRewarded, _loadingInterstitial, _disposed;
        private int _loadGeneration;
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
            public bool Rewarded, Done;
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
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

        /// <summary>Create on the Unity main thread: the SDK's callbacks are continued there.</summary>
        public AdMobProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _consent = new UmpConsentFlow(config.UnderAgeOfConsent, debugAllowed: UmpConsentFlow.DebugAllowedFor(config));
            _consent.Changed += OnConsentChanged;
        }

        private bool TestDevicesApply => _consent.DebugApplies;
        private void OnConsentChanged() { if (!_disposed) Changed?.Invoke(); }

        private void OnMain(Action action)
        {
            if (_main.IsCurrent) action();
            else _main.Post(action);
        }

        // ----- IConsentFlow

        public bool Completed => !_disposed && _consent.Completed;
        public bool CanRequestAds => !_disposed && _consent.CanRequestAds;
        public bool PrivacyOptionsRequired => !_disposed && _consent.PrivacyOptionsRequired;
        public event Action Changed;

        public void Request(Action onCompleted) { if (!_disposed) _consent.Request(onCompleted); }

        public void ShowPrivacyOptions(Action onClosed)
        {
            if (_disposed) return;
            _consent.ShowPrivacyOptions(() =>
            {
                if (_disposed) return;
                if (!ConsentInformation.CanRequestAds()) DropLoadedAds();
                onClosed?.Invoke();
            });
        }

        private void DropLoadedAds()
        {
            _loadGeneration++;
            _loadingRewarded = _loadingInterstitial = false;
            try { Release(_rewardedState); }
            finally
            {
                try { Release(_interstitialState); }
                finally
                {
                    var banner = _banner;
                    _banner = null;
                    banner?.Destroy();
                }
            }
        }

        private void Release(AdState ad)
        {
            if (ad == null) return;
            if (ReferenceEquals(_rewardedState, ad)) { _rewardedState = null; _rewarded = null; }
            if (ReferenceEquals(_interstitialState, ad)) { _interstitialState = null; _interstitial = null; }
            // No old-ad collection: release the handle, retaining only identity in callbacks already queued.
            // This does not guarantee that a native SDK delivers further callbacks after destruction.
            ad.Release();
        }

        private void Retire(AdState ad)
        {
            if (ad?.Show != null)
            {
                ad.Show.Done = true;
                if (ReferenceEquals(_show, ad.Show)) _show = null;
            }
            Release(ad);
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
            if (_disposed) throw new ObjectDisposedException(nameof(AdMobProvider));
            if (_sdkStarted) { onReady?.Invoke(true); return; }
            MobileAds.SetRequestConfiguration(new RequestConfiguration
            {
                MaxAdContentRating = MaxAdContentRating.ToMaxAdContentRating(config.MaxAdContentRating),
                AgeRestrictedTreatment = config.ChildDirected || config.UnderAgeOfConsent ? AgeRestrictedTreatment.Child : AgeRestrictedTreatment.Unspecified,
                TestDeviceIds = TestDevicesApply ? new List<string>(UmpConsentFlow.DebugDevices) : new List<string>(),
            });
            MobileAds.Initialize(_ => OnMain(() =>
            {
                if (_disposed) return;
                _sdkStarted = true;
                onReady?.Invoke(true); // the plugin reports adapter states, never a failed start
            }));
        }

        public void Load(AdFormat format)
        {
            if (_disposed) return;
            string unit = _config.AdUnitId(format);
            if (!_sdkStarted) { LoadFailed?.Invoke(format, "SDK not initialized"); return; }
            if (!ConsentInformation.CanRequestAds()) { LoadFailed?.Invoke(format, "consent does not allow ad requests"); return; }
            if (string.IsNullOrEmpty(unit)) { LoadFailed?.Invoke(format, "no ad unit id for " + format); return; }
            int generation = _loadGeneration;
            switch (format)
            {
                case AdFormat.Rewarded:
                    if (_loadingRewarded) return;
                    _loadingRewarded = true;
                    Retire(_rewardedState);
                    if (_disposed || generation != _loadGeneration) return;
                    RewardedAd.Load(unit, new AdRequest(), (ad, error) => OnMain(() =>
                    {
                        if (_disposed || generation != _loadGeneration) { ad?.Destroy(); return; }
                        _loadingRewarded = false;
                        if (!ConsentInformation.CanRequestAds()) { ad?.Destroy(); return; }
                        if (error != null || ad == null) { ad?.Destroy(); LoadFailed?.Invoke(format, error?.GetMessage() ?? "no ad"); return; }
                        var state = new AdState(format, ad.Destroy);
                        _rewardedState = state;
                        _rewarded = ad;
                        ad.OnAdFullScreenContentOpened += () => OnMain(() => OnOpened(state));
                        ad.OnAdFullScreenContentClosed += () => OnMain(() => Finish(state.Show, null));
                        ad.OnAdFullScreenContentFailed += e => OnMain(() => Finish(state.Show, AdResult.Failed, e?.GetMessage()));
                        ad.OnAdPaid += value => OnMain(() =>
                        {
                            if (!_disposed) Paid(value, format, state.Show?.Placement ?? "", ad.GetResponseInfo());
                        });
                        Loaded?.Invoke(format);
                    }));
                    return;
                case AdFormat.Interstitial:
                    if (_loadingInterstitial) return;
                    _loadingInterstitial = true;
                    Retire(_interstitialState);
                    if (_disposed || generation != _loadGeneration) return;
                    InterstitialAd.Load(unit, new AdRequest(), (ad, error) => OnMain(() =>
                    {
                        if (_disposed || generation != _loadGeneration) { ad?.Destroy(); return; }
                        _loadingInterstitial = false;
                        if (!ConsentInformation.CanRequestAds()) { ad?.Destroy(); return; }
                        if (error != null || ad == null) { ad?.Destroy(); LoadFailed?.Invoke(format, error?.GetMessage() ?? "no ad"); return; }
                        var state = new AdState(format, ad.Destroy);
                        _interstitialState = state;
                        _interstitial = ad;
                        ad.OnAdFullScreenContentOpened += () => OnMain(() => OnOpened(state));
                        ad.OnAdFullScreenContentClosed += () => OnMain(() => Finish(state.Show, null));
                        ad.OnAdFullScreenContentFailed += e => OnMain(() => Finish(state.Show, AdResult.Failed, e?.GetMessage()));
                        ad.OnAdPaid += value => OnMain(() =>
                        {
                            if (!_disposed) Paid(value, format, state.Show?.Placement ?? "", ad.GetResponseInfo());
                        });
                        Loaded?.Invoke(format);
                    }));
                    return;
                default:
                    LoadFailed?.Invoke(format, format + " loads through ShowBanner");
                    return;
            }
        }

        public bool IsReady(AdFormat format)
        {
            if (_disposed || !_sdkStarted || !ConsentInformation.CanRequestAds()) return false;
            switch (format)
            {
                case AdFormat.Rewarded: return _rewarded != null && _rewardedState.Show == null && _rewarded.CanShowAd();
                case AdFormat.Interstitial: return _interstitial != null && _interstitialState.Show == null && _interstitial.CanShowAd();
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
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
            if (format == AdFormat.Rewarded)
                show.Fault = RewardFaults.BeginShow(placement, () => OnRewarded(show), () => ad.Released);
#endif
            try
            {
                if (format == AdFormat.Rewarded) _rewarded.Show(_ => OnMain(() => ReceiveReward(show)));
                else _interstitial.Show();
            }
            catch { Retire(ad); throw; }
        }

        private void OnOpened(AdState ad)
        {
            if (!_disposed && !ad.Released && ad.Show != null && !ad.Show.Done && ReferenceEquals(_show, ad.Show))
            {
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
                if (ad.Format == AdFormat.Rewarded) _rewardFaults?.Opened(ad.Show.Fault);
#endif
                Opened?.Invoke(ad.Format);
            }
        }

        private void ReceiveReward(ShowState show)
        {
            if (_disposed) return;
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
            RewardFaults.Receive(show.Fault);
#else
            OnRewarded(show);
#endif
        }

        private void OnRewarded(ShowState show)
        {
            if (_disposed || show.Rewarded) return;
            show.Rewarded = true;
            show.OnRewardEarned?.Invoke();
        }

        /// <summary>Ends the show once; a null result is a close, paid when the reward arrived.</summary>
        private void Finish(ShowState show, AdResult? result, string reason = null)
        {
            if (_disposed || show == null || show.Done) return;
            show.Done = true;
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
            _rewardFaults?.Closed(show.Fault);
#endif
            if (ReferenceEquals(_show, show)) _show = null;
            if (reason != null) Debug.LogWarning("[Nexus Ads] AdMob " + show.Ad.Format + " failed to show: " + reason);
            try { Release(show.Ad); }
            finally { show.OnResult(result ?? (show.Rewarded ? AdResult.Rewarded : AdResult.Closed)); }
        }

        public void ShowBanner(string placement, BannerPosition position)
        {
            string unit = _config.AdUnitId(AdFormat.Banner);
            if (_disposed || !_sdkStarted || !ConsentInformation.CanRequestAds() || string.IsNullOrEmpty(unit)) return;
            _bannerPlacement = placement ?? "";
            var anchor = position == BannerPosition.Top ? AdPosition.Top : AdPosition.Bottom;
            if (_banner == null)
            {
                var banner = _banner = new BannerView(unit, AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSizeWithWidth(AdSize.FullWidth), anchor);
                banner.OnBannerAdLoadFailed += error => OnMain(() =>
                {
                    if (!_disposed && ReferenceEquals(_banner, banner))
                        Debug.LogWarning("[Nexus Ads] AdMob banner did not load: " + error?.GetMessage());
                });
                banner.OnAdPaid += value => OnMain(() =>
                {
                    if (!_disposed && ReferenceEquals(_banner, banner))
                        Paid(value, AdFormat.Banner, _bannerPlacement, banner.GetResponseInfo());
                });
                banner.LoadAd(new AdRequest());
            }
            else _banner.SetPosition(anchor);
            if (!_disposed) _banner?.Show();
        }

        public void HideBanner() => _banner?.Hide();

        public void OpenTestSuite()
        {
            if (_disposed) return;
            MobileAds.OpenAdInspector(error =>
            {
                if (!_disposed && error != null) Debug.LogWarning("[Nexus Ads] AdMob ad inspector: " + error.GetMessage());
            });
        }

        void IDisposable.Dispose()
        {
            if (_disposed) return;
            _disposed = true;
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
            _rewardFaults?.Dispose();
#endif
            _sdkStarted = false;
            if (_show != null) _show.Done = true;
            _show = null;
            _consent.Changed -= OnConsentChanged;
            try { ((IDisposable)_consent).Dispose(); }
            finally
            {
                try { DropLoadedAds(); }
                finally
                {
                    Changed = null;
                    Loaded = null;
                    LoadFailed = null;
                    Opened = null;
                    Revenue = null;
                }
            }
        }

        private void Paid(AdValue value, AdFormat format, string placement, ResponseInfo info)
        {
            if (value == null) return;
            string network = info?.GetLoadedAdapterResponseInfo()?.AdSourceName;
            Revenue?.Invoke(new AdRevenue(string.IsNullOrEmpty(network) ? ProviderName : network, placement, format,
                value.Value / 1_000_000.0, value.CurrencyCode ?? "", Precision(value.Precision)));
        }

        /// <summary>Google's precision in the family's words (<see cref="AdRevenue.Precision"/>).</summary>
        private static string Precision(AdValue.PrecisionType precision)
        {
            switch (precision)
            {
                case AdValue.PrecisionType.Precise: return "exact";
                case AdValue.PrecisionType.Estimated: return "estimated";
                case AdValue.PrecisionType.PublisherProvided: return "publisher_defined";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// Registers the provider and Google's consent form with the bootstrap before any scene loads; only the selected
    /// mediation is ever created, and with another mediation selected the UMP flow runs before it (issue #63).
    /// </summary>
    public static class AdMobRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
            AdsBootstrap.RegisterProvider(AdProviderKind.AdMob, (scheduler, config) => new AdMobProvider(config, scheduler));
#else
            AdsBootstrap.RegisterProvider(AdProviderKind.AdMob, (scheduler, config) => new AdMobProvider(config));
#endif
            AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GoogleUmp, config => new UmpConsentFlow(config.UnderAgeOfConsent, debugAllowed: UmpConsentFlow.DebugAllowedFor(config)));
        }
    }
}
