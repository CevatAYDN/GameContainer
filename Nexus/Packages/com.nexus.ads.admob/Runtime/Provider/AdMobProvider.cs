using System;
using System.Collections.Generic;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Google AdMob (Google Mobile Ads Unity plugin 11.3.0+, written against 11.6.0) as the core's
    /// <see cref="IAdProvider"/>, with its User Messaging Platform consent as <see cref="IConsentFlow"/>.
    /// Consent: <see cref="Request"/> asks UMP for an update on every launch, as Google's privacy page
    /// requires, and shows the form where the law needs it; consent kept from an earlier session lets
    /// ads load while this session's update runs. The age treatment (child-directed or under the age of
    /// consent: <c>AgeRestrictedTreatment.Child</c>, which replaced the deprecated TFCD and TFUA tags in 11.3.0)
    /// and the content rating reach <c>RequestConfiguration</c> before <c>MobileAds.Initialize</c>. Every SDK
    /// callback (the UMP listeners arrive on the Android UI thread) continues on the main thread.
    /// Google has no test-mode flag: development builds reach the SDK only with Google's sample units
    /// (<see cref="ProviderGate"/>), and <see cref="SetTestDevices"/> registers Google-format test device
    /// ids for those builds only. The plugin reads the App ID from its own settings asset; the validator
    /// keeps that asset equal to the settings tab.
    /// </summary>
    public sealed class AdMobProvider : IAdProvider, IConsentFlow
    {
        public const string ProviderName = "admob";

        private static readonly List<string> TestDevices = new List<string>();
        private static bool _eeaConsentGeography;

        /// <summary>
        /// Google-format test device ids: the 32-character id the SDK prints in logcat ("setTestDeviceIds")
        /// or in the Xcode console ("testDeviceIdentifiers"); on Android UMP prints the same id for its debug
        /// settings, on iOS add the id UMP prints as well. <paramref name="eeaConsentGeography"/> makes those
        /// devices see the consent form as in the EEA. Applied only while the configuration runs on Google's
        /// sample units, so a release build never carries them. Call before the bootstrap starts.
        /// </summary>
        public static void SetTestDevices(IEnumerable<string> googleTestDeviceIds, bool eeaConsentGeography = false)
        {
            TestDevices.Clear();
            if (googleTestDeviceIds != null)
                foreach (var id in googleTestDeviceIds) if (!string.IsNullOrWhiteSpace(id)) TestDevices.Add(id.Trim());
            _eeaConsentGeography = eeaConsentGeography;
        }

        private readonly ProviderConfig _config;
        private readonly UnityMainThread _main = new UnityMainThread();
        private RewardedAd _rewarded;
        private InterstitialAd _interstitial;
        private BannerView _banner;
        private string _bannerPlacement = "";
        private bool _sdkStarted, _loadingRewarded, _loadingInterstitial, _consentRequested, _consentAnswered;
        private Action _onConsent;
        private ShowState _show;

        private sealed class ShowState
        {
            public AdFormat Format;
            public string Placement;
            public Action<AdResult> OnResult;
            public bool Rewarded, Done;
        }

        /// <summary>Create on the Unity main thread: the SDK's callbacks are continued there.</summary>
        public AdMobProvider(ProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private bool TestDevicesApply => _config.UsesPublishedTestData && (TestDevices.Count > 0 || _eeaConsentGeography);

        private void OnMain(Action action)
        {
            if (_main.IsCurrent) action();
            else _main.Post(action);
        }

        // ----- IConsentFlow

        public bool Completed => _consentAnswered;
        public bool CanRequestAds => ConsentInformation.CanRequestAds();
        public bool PrivacyOptionsRequired => _consentAnswered && ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;
        public event Action Changed;

        public void Request(Action onCompleted)
        {
            if (_consentAnswered) { onCompleted?.Invoke(); return; }
            _onConsent += onCompleted;
            if (_consentRequested) return;
            _consentRequested = true;
            var request = new ConsentRequestParameters { TagForUnderAgeOfConsent = _config.UnderAgeOfConsent };
            if (TestDevicesApply)
                request.ConsentDebugSettings = new ConsentDebugSettings
                {
                    DebugGeography = _eeaConsentGeography ? DebugGeography.EEA : DebugGeography.Disabled,
                    TestDeviceHashedIds = new List<string>(TestDevices),
                };
            ConsentInformation.Update(request, updateError => OnMain(() =>
            {
                if (updateError != null) Debug.LogWarning("[Nexus Ads] AdMob consent update failed: " + updateError.Message);
                ConsentForm.LoadAndShowConsentFormIfRequired(formError => OnMain(() =>
                {
                    if (formError != null) Debug.LogWarning("[Nexus Ads] AdMob consent form: " + formError.Message);
                    AnswerConsent();
                    Changed?.Invoke();
                }));
            }));
            // consent obtained in an earlier session: ads may load while this session's update runs
            if (ConsentInformation.CanRequestAds()) AnswerConsent();
        }

        private void AnswerConsent()
        {
            _consentAnswered = true;
            var callback = _onConsent;
            _onConsent = null;
            callback?.Invoke();
        }

        public void ShowPrivacyOptions(Action onClosed)
        {
            ConsentForm.ShowPrivacyOptionsForm(error => OnMain(() =>
            {
                if (error != null) Debug.LogWarning("[Nexus Ads] AdMob privacy options: " + error.Message);
                if (!ConsentInformation.CanRequestAds()) DropLoadedAds();
                Changed?.Invoke();
                onClosed?.Invoke();
            }));
        }

        private void DropLoadedAds()
        {
            _rewarded?.Destroy(); _rewarded = null;
            _interstitial?.Destroy(); _interstitial = null;
            _banner?.Destroy(); _banner = null;
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
            if (_sdkStarted) { onReady?.Invoke(true); return; }
            MobileAds.SetRequestConfiguration(new RequestConfiguration
            {
                MaxAdContentRating = MaxAdContentRating.ToMaxAdContentRating(config.MaxAdContentRating),
                AgeRestrictedTreatment = config.ChildDirected || config.UnderAgeOfConsent ? AgeRestrictedTreatment.Child : AgeRestrictedTreatment.Unspecified,
                TestDeviceIds = TestDevicesApply ? new List<string>(TestDevices) : new List<string>(),
            });
            MobileAds.Initialize(_ => OnMain(() =>
            {
                _sdkStarted = true;
                onReady?.Invoke(true); // the plugin reports adapter states, never a failed start
            }));
        }

        public void Load(AdFormat format)
        {
            string unit = _config.AdUnitId(format);
            if (!_sdkStarted) { LoadFailed?.Invoke(format, "SDK not initialized"); return; }
            if (!ConsentInformation.CanRequestAds()) { LoadFailed?.Invoke(format, "consent does not allow ad requests"); return; }
            if (string.IsNullOrEmpty(unit)) { LoadFailed?.Invoke(format, "no ad unit id for " + format); return; }
            switch (format)
            {
                case AdFormat.Rewarded:
                    if (_loadingRewarded) return;
                    _loadingRewarded = true;
                    RewardedAd.Load(unit, new AdRequest(), (ad, error) => OnMain(() =>
                    {
                        _loadingRewarded = false;
                        if (error != null || ad == null) { LoadFailed?.Invoke(format, error?.GetMessage() ?? "no ad"); return; }
                        _rewarded?.Destroy();
                        _rewarded = ad;
                        ad.OnAdFullScreenContentOpened += () => OnMain(() => Opened?.Invoke(format));
                        ad.OnAdFullScreenContentClosed += () => OnMain(() => Finish(format, null));
                        ad.OnAdFullScreenContentFailed += e => OnMain(() => Finish(format, AdResult.Failed, e?.GetMessage()));
                        ad.OnAdPaid += value => OnMain(() => Paid(value, format, _show?.Placement ?? "", ad.GetResponseInfo()));
                        Loaded?.Invoke(format);
                    }));
                    return;
                case AdFormat.Interstitial:
                    if (_loadingInterstitial) return;
                    _loadingInterstitial = true;
                    InterstitialAd.Load(unit, new AdRequest(), (ad, error) => OnMain(() =>
                    {
                        _loadingInterstitial = false;
                        if (error != null || ad == null) { LoadFailed?.Invoke(format, error?.GetMessage() ?? "no ad"); return; }
                        _interstitial?.Destroy();
                        _interstitial = ad;
                        ad.OnAdFullScreenContentOpened += () => OnMain(() => Opened?.Invoke(format));
                        ad.OnAdFullScreenContentClosed += () => OnMain(() => Finish(format, null));
                        ad.OnAdFullScreenContentFailed += e => OnMain(() => Finish(format, AdResult.Failed, e?.GetMessage()));
                        ad.OnAdPaid += value => OnMain(() => Paid(value, format, _show?.Placement ?? "", ad.GetResponseInfo()));
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
            if (!_sdkStarted || !ConsentInformation.CanRequestAds()) return false;
            switch (format)
            {
                case AdFormat.Rewarded: return _rewarded != null && _rewarded.CanShowAd();
                case AdFormat.Interstitial: return _interstitial != null && _interstitial.CanShowAd();
                default: return false;
            }
        }

        public void Show(AdFormat format, string placement, Action<AdResult> onResult)
        {
            if (onResult == null) throw new ArgumentNullException(nameof(onResult));
            if (_show != null && !_show.Done) { onResult(AdResult.NotReady); return; } // one full-screen ad at a time; the guard blocks earlier
            if (!IsReady(format)) { onResult(AdResult.NotReady); return; }
            var show = _show = new ShowState { Format = format, Placement = placement ?? "", OnResult = onResult };
            if (format == AdFormat.Rewarded) _rewarded.Show(_ => OnMain(() => show.Rewarded = true)); // Google pays before the close
            else _interstitial.Show();
        }

        /// <summary>Ends the show once; a null result is a close, paid when the reward arrived.</summary>
        private void Finish(AdFormat format, AdResult? result, string reason = null)
        {
            var show = _show;
            if (show == null || show.Done || show.Format != format) return;
            show.Done = true;
            _show = null;
            if (reason != null) Debug.LogWarning("[Nexus Ads] AdMob " + format + " failed to show: " + reason);
            if (format == AdFormat.Rewarded) { _rewarded?.Destroy(); _rewarded = null; }
            else { _interstitial?.Destroy(); _interstitial = null; }
            show.OnResult(result ?? (show.Rewarded ? AdResult.Rewarded : AdResult.Closed));
        }

        public void ShowBanner(string placement, BannerPosition position)
        {
            string unit = _config.AdUnitId(AdFormat.Banner);
            if (!_sdkStarted || !ConsentInformation.CanRequestAds() || string.IsNullOrEmpty(unit)) return;
            _bannerPlacement = placement ?? "";
            var anchor = position == BannerPosition.Top ? AdPosition.Top : AdPosition.Bottom;
            if (_banner == null)
            {
                var banner = _banner = new BannerView(unit, AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSizeWithWidth(AdSize.FullWidth), anchor);
                banner.OnBannerAdLoadFailed += error => OnMain(() => Debug.LogWarning("[Nexus Ads] AdMob banner did not load: " + error?.GetMessage()));
                banner.OnAdPaid += value => OnMain(() => Paid(value, AdFormat.Banner, _bannerPlacement, banner.GetResponseInfo()));
                banner.LoadAd(new AdRequest());
            }
            else _banner.SetPosition(anchor);
            _banner.Show();
        }

        public void HideBanner() => _banner?.Hide();

        public void OpenTestSuite() => MobileAds.OpenAdInspector(error =>
        {
            if (error != null) Debug.LogWarning("[Nexus Ads] AdMob ad inspector: " + error.GetMessage()); // it opens on test devices only
        });

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

    /// <summary>Registers the provider with the bootstrap before any scene loads; only the selected mediation is ever created.</summary>
    public static class AdMobRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => AdsBootstrap.RegisterProvider(AdProviderKind.AdMob, (scheduler, config) => new AdMobProvider(config));
    }
}
