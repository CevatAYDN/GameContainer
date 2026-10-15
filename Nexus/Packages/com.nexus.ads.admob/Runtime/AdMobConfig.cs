using UnityEngine;

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Ad unit ids and request settings for <see cref="AdMobAdapter"/>. One ad unit per kind and
    /// platform; the service's placement strings are labels for the game, not ad units.
    /// </summary>
    public sealed class AdMobConfig
    {
        public string RewardedAdUnitIdAndroid;
        public string RewardedAdUnitIdIos;
        public string InterstitialAdUnitIdAndroid;
        public string InterstitialAdUnitIdIos;
        public string BannerAdUnitIdAndroid;
        public string BannerAdUnitIdIos;
        /// <summary>Google's content rating: "G", "PG", "T" or "MA".</summary>
        public string MaxAdContentRating = "G";
        public bool TagForUnderAgeOfConsent;
        /// <summary>Use an anchored adaptive banner of the screen width instead of the fixed 320x50 banner.</summary>
        public bool AdaptiveBanner = true;

        public string RewardedAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? RewardedAdUnitIdIos : RewardedAdUnitIdAndroid;
        public string InterstitialAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? InterstitialAdUnitIdIos : InterstitialAdUnitIdAndroid;
        public string BannerAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? BannerAdUnitIdIos : BannerAdUnitIdAndroid;
    }
}
