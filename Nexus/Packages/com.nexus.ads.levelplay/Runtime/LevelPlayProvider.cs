#if NEXUS_LEVELPLAY
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
    /// <see cref="LevelPlayRewardedAd"/> and one <see cref="LevelPlayInterstitialAd"/> per configured ad
    /// unit, a banner on demand, impression data as <see cref="AdRevenue"/>. The SDK fires
    /// <c>OnAdRewarded</c> after <c>OnAdClosed</c> at times (its own documentation says so), so a close
    /// waits <see cref="RewardGraceSeconds"/> for a late reward before it answers Closed. Test mode
    /// (the Test tab) enables the integration test suite metadata and adapter debug logging before
    /// Init; test ads themselves come from the devices listed in the LevelPlay dashboard.
    /// Consent: LevelPlay ships no form. GDPR consent collected by Google UMP or a compatible CMP is
    /// shared with the SDK automatically (SDK 7.7.0+); a game with another CMP calls
    /// <see cref="SetGdprConsent"/>. COPPA goes to <c>LevelPlayPrivacySettings.SetCOPPA</c> from the
    /// settings' child-directed flag before Init. Init waits for a network: offline, the SDK answers
    /// Init from its cached response and every load of the session ends in 509 (issue #125).
    /// </summary>
    public sealed class LevelPlayProvider : IAdProvider, IConsentFlow
    {
        public const string ProviderName = "levelplay";
        public const double RewardGraceSeconds = 1.5;
        public const double NetworkPollSeconds = 2;

        private readonly IAdScheduler _scheduler;
        private readonly Func<bool> _networkReachable;
        private IDisposable _networkWait;
        private readonly Dictionary<AdFormat, string> _units = new Dictionary<AdFormat, string>();
        private LevelPlayRewardedAd _rewarded;
        private LevelPlayInterstitialAd _interstitial;
        private LevelPlayBannerAd _banner;
        private Action<bool> _onReady;
        private bool _initialized;
        private ShowState _show;

        private sealed class ShowState
        {
            public AdFormat Format;
            public Action<AdResult> OnResult;
            public bool Rewarded, Closed, Done;
            public IDisposable Grace;
        }

        public LevelPlayProvider(IAdScheduler scheduler)
            : this(scheduler, () => Application.internetReachability != NetworkReachability.NotReachable) { }

        internal LevelPlayProvider(IAdScheduler scheduler, Func<bool> networkReachable)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _networkReachable = networkReachable ?? throw new ArgumentNullException(nameof(networkReachable));
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
            if (_initialized) { onReady?.Invoke(true); return; }
            _onReady = onReady;
            _units.Clear();
            foreach (var pair in config.AdUnitIds) _units[pair.Key] = pair.Value;

            // privacy and test settings go in before Init, as the SDK's regulation page asks
            LevelPlayPrivacySettings.SetCOPPA(config.ChildDirected || config.UnderAgeOfConsent);
            if (config.TestMode)
            {
                global::Unity.Services.LevelPlay.LevelPlay.SetMetaData("is_test_suite", "enable");
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
            if (!_networkReachable()) { _networkWait = _scheduler.Schedule(NetworkPollSeconds, () => InitWhenReachable(appKey)); return; }
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess += OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed += OnInitFailed;
            global::Unity.Services.LevelPlay.LevelPlay.Init(appKey); // the editor platform answers only on an Android or iOS build target; elsewhere the SDK is absent and nothing fires
        }

        private void OnInitSuccess(LevelPlayConfiguration configuration)
        {
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess -= OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed -= OnInitFailed;
            _initialized = true;
            if (_units.TryGetValue(AdFormat.Rewarded, out var rewardedUnit) && !string.IsNullOrEmpty(rewardedUnit))
            {
                _rewarded = new LevelPlayRewardedAd(rewardedUnit);
                _rewarded.OnAdLoaded += _ => Loaded?.Invoke(AdFormat.Rewarded);
                _rewarded.OnAdLoadFailed += error => LoadFailed?.Invoke(AdFormat.Rewarded, Describe(error));
                _rewarded.OnAdDisplayed += _ => Opened?.Invoke(AdFormat.Rewarded);
                _rewarded.OnAdDisplayFailed += (_, error) => Finish(AdFormat.Rewarded, AdResult.Failed);
                _rewarded.OnAdRewarded += (_, __) => OnRewarded();
                _rewarded.OnAdClosed += _ => OnClosed(AdFormat.Rewarded);
                _rewarded.OnAdImpressionDataReady += data => OnImpression(AdFormat.Rewarded, data);
            }
            if (_units.TryGetValue(AdFormat.Interstitial, out var interstitialUnit) && !string.IsNullOrEmpty(interstitialUnit))
            {
                _interstitial = new LevelPlayInterstitialAd(interstitialUnit);
                _interstitial.OnAdLoaded += _ => Loaded?.Invoke(AdFormat.Interstitial);
                _interstitial.OnAdLoadFailed += error => LoadFailed?.Invoke(AdFormat.Interstitial, Describe(error));
                _interstitial.OnAdDisplayed += _ => Opened?.Invoke(AdFormat.Interstitial);
                _interstitial.OnAdDisplayFailed += (_, error) => Finish(AdFormat.Interstitial, AdResult.Failed);
                _interstitial.OnAdClosed += _ => OnClosed(AdFormat.Interstitial);
                _interstitial.OnAdImpressionDataReady += data => OnImpression(AdFormat.Interstitial, data);
            }
            var callback = _onReady; _onReady = null;
            callback?.Invoke(true);
        }

        private void OnInitFailed(LevelPlayInitError error)
        {
            global::Unity.Services.LevelPlay.LevelPlay.OnInitSuccess -= OnInitSuccess;
            global::Unity.Services.LevelPlay.LevelPlay.OnInitFailed -= OnInitFailed;
            Debug.LogWarning("[Nexus Ads] LevelPlay init failed: " + error);
            var callback = _onReady; _onReady = null;
            callback?.Invoke(false);
        }

        public void Load(AdFormat format)
        {
            switch (format)
            {
                case AdFormat.Rewarded when _rewarded != null: _rewarded.LoadAd(); return;
                case AdFormat.Interstitial when _interstitial != null: _interstitial.LoadAd(); return;
                default: LoadFailed?.Invoke(format, _initialized ? "no ad unit id for " + format : "SDK not initialized"); return;
            }
        }

        public bool IsReady(AdFormat format)
        {
            switch (format)
            {
                case AdFormat.Rewarded: return _rewarded != null && _rewarded.IsAdReady();
                case AdFormat.Interstitial: return _interstitial != null && _interstitial.IsAdReady();
                default: return false;
            }
        }

        public void Show(AdFormat format, string placement, Action<AdResult> onResult)
        {
            if (onResult == null) throw new ArgumentNullException(nameof(onResult));
            if (_show != null && !_show.Done) { onResult(AdResult.NotReady); return; } // one full-screen ad at a time; the guard blocks earlier
            if (!IsReady(format)) { onResult(AdResult.NotReady); return; }
            var show = _show = new ShowState { Format = format, OnResult = onResult };
            try { if (format == AdFormat.Rewarded) _rewarded.ShowAd(placement); else _interstitial.ShowAd(placement); }
            catch { if (_show == show) _show = null; throw; } // an open show would answer NotReady to every later one (#151)
        }

        private void OnRewarded()
        {
            var show = _show;
            if (show == null || show.Done || show.Format != AdFormat.Rewarded) return;
            show.Rewarded = true;
            if (show.Closed) Finish(AdFormat.Rewarded, AdResult.Rewarded); // the late reward the SDK documents
        }

        private void OnClosed(AdFormat format)
        {
            var show = _show;
            if (show == null || show.Done || show.Format != format) return;
            show.Closed = true;
            if (format != AdFormat.Rewarded) { Finish(format, AdResult.Closed); return; }
            if (show.Rewarded) { Finish(format, AdResult.Rewarded); return; }
            show.Grace = _scheduler.Schedule(RewardGraceSeconds, () => Finish(AdFormat.Rewarded, show.Rewarded ? AdResult.Rewarded : AdResult.Closed));
        }

        private void Finish(AdFormat format, AdResult result)
        {
            var show = _show;
            if (show == null || show.Done || show.Format != format) return;
            show.Done = true;
            show.Grace?.Dispose();
            _show = null;
            show.OnResult(result);
        }

        public void ShowBanner(string placement, BannerPosition position)
        {
            if (!_initialized || !_units.TryGetValue(AdFormat.Banner, out var unit) || string.IsNullOrEmpty(unit)) return;
            if (_banner == null)
            {
                var config = new LevelPlayBannerAd.Config.Builder()
                    .SetSize(LevelPlayAdSize.CreateAdaptiveAdSize())
                    .SetPosition(position == BannerPosition.Top ? LevelPlayBannerPosition.TopCenter : LevelPlayBannerPosition.BottomCenter)
                    .SetPlacementName(placement)
                    .SetDisplayOnLoad(true)
                    .Build();
                _banner = new LevelPlayBannerAd(unit, config);
                _banner.OnAdLoadFailed += error => Debug.LogWarning("[Nexus Ads] LevelPlay banner did not load: " + Describe(error));
                _banner.OnAdImpressionDataReady += data => OnImpression(AdFormat.Banner, data);
                _banner.LoadAd();
                return;
            }
            _banner.ShowAd();
        }

        public void HideBanner() => _banner?.HideAd();

        public void OpenTestSuite() => global::Unity.Services.LevelPlay.LevelPlay.LaunchTestSuite();

        // the format comes from the ad object, not data.AdFormat: the device SDK sends "rewarded_video", the editor SDK "editor_mock_format"
        private void OnImpression(AdFormat format, LevelPlayImpressionData data)
        {
            if (data == null) return;
            Revenue?.Invoke(new AdRevenue(data.AdNetwork ?? ProviderName, data.Placement ?? "", format, data.Revenue ?? 0, "USD", data.Precision ?? ""));
        }

        private static string Describe(LevelPlayAdError error) => error == null ? "unknown" : error.ErrorCode + " " + error.ErrorMessage;

        // ----- IConsentFlow: LevelPlay ships no form; consent arrives from the game's CMP (Google UMP is shared automatically).

        public bool Completed => true;
        public bool CanRequestAds => true;
        public bool PrivacyOptionsRequired => false;
        public event Action Changed { add { } remove { } }
        public void Request(Action onCompleted) => onCompleted?.Invoke();
        public void ShowPrivacyOptions(Action onClosed) => onClosed?.Invoke();

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
