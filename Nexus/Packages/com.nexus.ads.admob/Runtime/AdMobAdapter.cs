#if NEXUS_ADMOB
using System;
using System.Threading.Tasks;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Google AdMob behind <see cref="AdService"/>. Consent first (the User Messaging Platform asks
    /// where the law requires it), then the SDK starts and one rewarded and one interstitial ad are
    /// kept loaded; a failed load is retried with a growing pause. The game stays playable without
    /// ads (offline, no consent, no fill). Ported from the first shipped Nexus game's adapter.
    /// </summary>
    public sealed class AdMobAdapter : IAdNetworkAdapter, IAdOutcomeAdapter, IAdReadinessAdapter, IAdConsentAdapter
    {
        public const string ProviderName = "admob";

        /// <summary>Registers the adapter with the factory under <see cref="ProviderName"/>.</summary>
        public static void Register(IAdAdapterFactory factory, AdMobConfig config)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (config == null) throw new ArgumentNullException(nameof(config));
            factory.RegisterProvider(ProviderName, () => new AdMobAdapter(config));
        }

        private readonly AdMobConfig _config;
        private RewardedAd _rewarded;
        private InterstitialAd _interstitial;
        private BannerView _banner;
        private bool _consentAsked, _sdkStarted, _loadingRewarded, _loadingInterstitial;
        private int _failedRewardedLoads, _failedInterstitialLoads;

        public event Action ReadyChanged;
        public bool PrivacyOptionsRequired => _consentAsked && ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;

        public AdMobAdapter(AdMobConfig config) { _config = config ?? throw new ArgumentNullException(nameof(config)); }

        public void Initialize(Action onInitialized)
        {
            if (_consentAsked) { onInitialized?.Invoke(); return; }
            _consentAsked = true;
            MobileAds.RaiseAdEventsOnUnityMainThread = true;
            var request = new ConsentRequestParameters { TagForUnderAgeOfConsent = _config.TagForUnderAgeOfConsent };
            ConsentInformation.Update(request, updateError =>
            {
                if (updateError != null) Warn("Consent update failed: " + updateError.Message);
                ConsentForm.LoadAndShowConsentFormIfRequired(formError =>
                {
                    if (formError != null) Warn("Consent form: " + formError.Message);
                    Changed();
                    if (ConsentInformation.CanRequestAds()) StartSdk(onInitialized);
                    else onInitialized?.Invoke();
                });
            });
            // Consent given in an earlier session: ads can be requested while this session's update runs.
            if (ConsentInformation.CanRequestAds()) StartSdk(onInitialized);
        }

        private void StartSdk(Action onInitialized)
        {
            if (_sdkStarted) { onInitialized?.Invoke(); return; }
            _sdkStarted = true;
            MobileAds.SetRequestConfiguration(new RequestConfiguration
            {
                MaxAdContentRating = MaxAdContentRating.ToMaxAdContentRating(_config.MaxAdContentRating),
                TagForUnderAgeOfConsent = _config.TagForUnderAgeOfConsent ? TagForUnderAgeOfConsent.True : TagForUnderAgeOfConsent.False,
            });
            MobileAds.Initialize(_ =>
            {
                LoadRewarded();
                LoadInterstitial();
                Changed();
                onInitialized?.Invoke();
            });
        }

        private bool CanLoad => _sdkStarted && ConsentInformation.CanRequestAds();

        // ----- rewarded
        public bool IsRewardedReady(string placement) => CanLoad && _rewarded != null && _rewarded.CanShowAd();

        public void ShowRewarded(string placement, Action<bool> onCompleted)
            => RequestReward(placement, outcome => onCompleted?.Invoke(outcome == AdOutcome.Rewarded));

        public void RequestReward(string placement, Action<AdOutcome> onOutcome)
        {
            if (!IsRewardedReady(placement)) { onOutcome?.Invoke(AdOutcome.NotReady); LoadRewarded(); return; }
            var ad = _rewarded;
            _rewarded = null;
            bool rewarded = false, finished = false;
            void Finish(AdOutcome outcome)
            {
                if (finished) return;
                finished = true;
                ad.Destroy();
                Changed();
                LoadRewarded();
                onOutcome?.Invoke(outcome);
            }
            ad.OnAdFullScreenContentClosed += () => Finish(rewarded ? AdOutcome.Rewarded : AdOutcome.Closed);
            ad.OnAdFullScreenContentFailed += error => { Warn("Rewarded ad failed to show: " + error?.GetMessage()); Finish(AdOutcome.Failed); };
            ad.Show(_ => rewarded = true);
        }

        private void LoadRewarded()
        {
            if (_loadingRewarded || !CanLoad || string.IsNullOrEmpty(_config.RewardedAdUnitId) || (_rewarded != null && _rewarded.CanShowAd())) return;
            _loadingRewarded = true;
            RewardedAd.Load(_config.RewardedAdUnitId, new AdRequest(), (ad, error) =>
            {
                _loadingRewarded = false;
                if (error != null || ad == null)
                {
                    _failedRewardedLoads++;
                    Warn("Rewarded ad did not load: " + (error?.GetMessage() ?? "no ad"));
                    RetryLater(_failedRewardedLoads, LoadRewarded);
                    return;
                }
                _failedRewardedLoads = 0;
                _rewarded = ad;
                Changed();
            });
        }

        // ----- interstitial
        public bool IsInterstitialReady(string placement) => CanLoad && _interstitial != null && _interstitial.CanShowAd();

        public void ShowInterstitial(string placement, Action onClosed)
        {
            if (!IsInterstitialReady(placement)) { onClosed?.Invoke(); LoadInterstitial(); return; }
            var ad = _interstitial;
            _interstitial = null;
            bool finished = false;
            void Finish()
            {
                if (finished) return;
                finished = true;
                ad.Destroy();
                Changed();
                LoadInterstitial();
                onClosed?.Invoke();
            }
            ad.OnAdFullScreenContentClosed += Finish;
            ad.OnAdFullScreenContentFailed += error => { Warn("Interstitial failed to show: " + error?.GetMessage()); Finish(); };
            ad.Show();
        }

        private void LoadInterstitial()
        {
            if (_loadingInterstitial || !CanLoad || string.IsNullOrEmpty(_config.InterstitialAdUnitId) || (_interstitial != null && _interstitial.CanShowAd())) return;
            _loadingInterstitial = true;
            InterstitialAd.Load(_config.InterstitialAdUnitId, new AdRequest(), (ad, error) =>
            {
                _loadingInterstitial = false;
                if (error != null || ad == null)
                {
                    _failedInterstitialLoads++;
                    Warn("Interstitial did not load: " + (error?.GetMessage() ?? "no ad"));
                    RetryLater(_failedInterstitialLoads, LoadInterstitial);
                    return;
                }
                _failedInterstitialLoads = 0;
                _interstitial = ad;
                Changed();
            });
        }

        // ----- banner
        public void ShowBanner(string placement, string position)
        {
            if (!CanLoad || string.IsNullOrEmpty(_config.BannerAdUnitId)) return;
            var anchor = string.Equals(position, "top", StringComparison.OrdinalIgnoreCase) ? AdPosition.Top : AdPosition.Bottom;
            if (_banner == null)
            {
                var size = _config.AdaptiveBanner ? AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSizeWithWidth(AdSize.FullWidth) : AdSize.Banner;
                _banner = new BannerView(_config.BannerAdUnitId, size, anchor);
                _banner.OnBannerAdLoadFailed += error => Warn("Banner did not load: " + error?.GetMessage());
                _banner.LoadAd(new AdRequest());
            }
            else _banner.SetPosition(anchor);
            _banner.Show();
        }

        public void HideBanner() => _banner?.Hide();

        // ----- consent
        public void ShowPrivacyOptions(Action onClosed) => ConsentForm.ShowPrivacyOptionsForm(error =>
        {
            if (error != null) Warn("Privacy options: " + error.Message);
            if (!ConsentInformation.CanRequestAds())
            {
                _rewarded?.Destroy(); _rewarded = null;
                _interstitial?.Destroy(); _interstitial = null;
                _banner?.Destroy(); _banner = null;
            }
            else { StartSdk(null); LoadRewarded(); LoadInterstitial(); }
            Changed();
            onClosed?.Invoke();
        });

        /// <summary>30 s, 60 s, 2 min ... up to 10 min between attempts, on the main thread. Quitting cancels the wait.</summary>
        private static async void RetryLater(int failures, Action load)
        {
            int seconds = Math.Min(600, 30 * (1 << Math.Min(5, Math.Max(0, failures - 1))));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), Application.exitCancellationToken); }
            catch (OperationCanceledException) { return; }
            load();
        }

        private void Changed() => ReadyChanged?.Invoke();
        private static void Warn(string message) => NexusLog.WarnAlways("[AdMob] " + message);
    }
}
#endif
