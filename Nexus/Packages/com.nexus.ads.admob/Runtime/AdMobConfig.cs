using System.Collections.Generic;
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
        /// <summary>Testing only: the consent form treats this device as one in the EEA (UMP applies it to the devices in
        /// <see cref="TestDeviceHashedIds"/>). Left false with no ids, the request carries no debug settings, as before.</summary>
        public bool DebugGeographyEea;
        /// <summary>Testing only: UMP's hashed ids of the test devices (the SDK prints a device's id in logcat on its first request).</summary>
        public List<string> TestDeviceHashedIds;

        internal bool WantsConsentDebug => DebugGeographyEea || TestDeviceHashedIds?.Count > 0;
        /// <summary>Use an anchored adaptive banner of the screen width instead of the fixed 320x50 banner.</summary>
        public bool AdaptiveBanner = true;

        public string RewardedAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? RewardedAdUnitIdIos : RewardedAdUnitIdAndroid;
        public string InterstitialAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? InterstitialAdUnitIdIos : InterstitialAdUnitIdAndroid;
        public string BannerAdUnitId => Application.platform == RuntimePlatform.IPhonePlayer ? BannerAdUnitIdIos : BannerAdUnitIdAndroid;
    }
}
