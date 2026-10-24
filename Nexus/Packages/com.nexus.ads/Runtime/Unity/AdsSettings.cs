using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>The mediation (or AdMob alone) a build ships with.</summary>
    public enum AdProviderKind { None, LevelPlay, Max, AdMob }

    public enum AdPlatform { Android, Ios }

    /// <summary>Google's content-rating scale; every provider maps it to its own.</summary>
    public enum ContentRating { G, PG, T, MA }

    [Serializable]
    public sealed class PlatformKeys
    {
        [Tooltip("LevelPlay App Key, MAX SDK key, or the AdMob App ID")]
        public string AppKey = "";
        public string RewardedUnitId = "";
        public string InterstitialUnitId = "";
        public string BannerUnitId = "";

        public string UnitId(AdFormat format)
        {
            switch (format)
            {
                case AdFormat.Rewarded: return RewardedUnitId;
                case AdFormat.Interstitial: return InterstitialUnitId;
                default: return BannerUnitId;
            }
        }

        public void SetUnitId(AdFormat format, string value)
        {
            switch (format)
            {
                case AdFormat.Rewarded: RewardedUnitId = value; break;
                case AdFormat.Interstitial: InterstitialUnitId = value; break;
                default: BannerUnitId = value; break;
            }
        }

        public PlatformKeys Clone() => new PlatformKeys { AppKey = AppKey, RewardedUnitId = RewardedUnitId, InterstitialUnitId = InterstitialUnitId, BannerUnitId = BannerUnitId };
    }

    [Serializable]
    public sealed class ProviderKeys
    {
        public PlatformKeys Android = new PlatformKeys();
        public PlatformKeys Ios = new PlatformKeys();
        public PlatformKeys For(AdPlatform platform) => platform == AdPlatform.Ios ? Ios : Android;
    }

    /// <summary>One tab's keys for every provider.</summary>
    [Serializable]
    public sealed class ProviderTab
    {
        public ProviderKeys LevelPlay = new ProviderKeys();
        public ProviderKeys Max = new ProviderKeys();
        public ProviderKeys AdMob = new ProviderKeys();

        public ProviderKeys For(AdProviderKind kind)
        {
            switch (kind)
            {
                case AdProviderKind.LevelPlay: return LevelPlay;
                case AdProviderKind.Max: return Max;
                case AdProviderKind.AdMob: return AdMob;
                default: return null;
            }
        }
    }

    [Serializable]
    public sealed class PlacementEntry
    {
        public string Name = "";
        public AdFormat Format = AdFormat.Rewarded;
        [Tooltip("The placement sits at a natural pause of play; interstitials show nowhere else")]
        public bool NaturalBreak;
        public double CooldownSeconds;
        [Tooltip("0 = no cap")] public int SessionCap;
        [Tooltip("0 = no cap")] public int DayCap;
        public double FirstShowAfterSeconds;

        public AdPlacement ToPlacement() => new AdPlacement(Name, Format, NaturalBreak, CooldownSeconds, SessionCap, DayCap, FirstShowAfterSeconds);
    }

    /// <summary>The policy floors as the editor shows them; values below <see cref="AdPolicy.Default"/> are raised to it.</summary>
    [Serializable]
    public sealed class PolicyEntry
    {
        public double MinInterstitialGapSeconds = 30;
        public double NoInterstitialForFirstSeconds = 60;
        public double NoInterstitialAfterRewardedSeconds = 30;
        [Tooltip("0 = no cap")] public int MaxInterstitialsPerSession;
        [Tooltip("0 = no cap")] public int MaxInterstitialsPerDay;
        public double SessionResumeGapMinutes = 30;

        public AdPolicy ToPolicy() => AdPolicy.Default.Tighten(new AdPolicy(
            Math.Max(0, MinInterstitialGapSeconds), Math.Max(0, NoInterstitialForFirstSeconds), Math.Max(0, NoInterstitialAfterRewardedSeconds),
            Math.Max(0, MaxInterstitialsPerSession), Math.Max(0, MaxInterstitialsPerDay), Math.Max(0, SessionResumeGapMinutes)));
    }

    /// <summary>
    /// The one settings asset of the ads package family. The editor and development builds read the
    /// Test tab, release builds the Release tab, and nothing can change that mapping
    /// (<see cref="TabFor"/>). Loaded at runtime from <c>Resources/NexusAdsSettings</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Nexus/Ads Settings", fileName = ResourceName)]
    public sealed class AdsSettings : ScriptableObject
    {
        public const string ResourceName = "NexusAdsSettings";

        [Header("Mediation")]
        public AdProviderKind SelectedMediation = AdProviderKind.None;
        [Tooltip("A release build without ads, on purpose; reported in the validator and the build log")]
        public bool AdFreeRelease;

        [Header("Keys")]
        [Tooltip("The provider's official test data, or a copy of the Release keys with test mode; used by the editor and development builds")]
        public ProviderTab Test = new ProviderTab();
        [Tooltip("The live account keys; used by release builds only")]
        public ProviderTab Release = new ProviderTab();
        [Tooltip("Advertising ids (GAID / IDFA) of the devices that may request live SDK ads in a development build")]
        public List<string> TestDeviceIds = new List<string>();

        [Header("Placements")]
        public List<PlacementEntry> Placements = new List<PlacementEntry>();

        [Header("Policy floors")]
        public PolicyEntry Policy = new PolicyEntry();

        [Header("Privacy")]
        public bool ChildDirected;
        public bool UnderAgeOfConsent;
        public ContentRating MaxAdContentRating = ContentRating.G;
        public string PrivacyPolicyUrl = "";

        public static AdsSettings Load() => Resources.Load<AdsSettings>(ResourceName);

        /// <summary>The tab a build kind reads; fixed by design.</summary>
        public static bool UsesReleaseTab(BuildKind build) => build == BuildKind.Release;

        public ProviderTab TabFor(BuildKind build) => UsesReleaseTab(build) ? Release : Test;

        public IReadOnlyList<AdPlacement> ToPlacements()
        {
            var list = new List<AdPlacement>(Placements.Count);
            for (int i = 0; i < Placements.Count; i++) list.Add(Placements[i].ToPlacement());
            return list;
        }

        /// <summary>True when a placement uses the format; only those formats need an ad unit id.</summary>
        public bool Uses(AdFormat format)
        {
            for (int i = 0; i < Placements.Count; i++) if (Placements[i].Format == format) return true;
            return false;
        }

        /// <summary>The fields a provider must fill on a tab, derived from the placements: the app key and one unit id per used format.</summary>
        public List<string> MissingFields(AdProviderKind kind, AdPlatform platform, ProviderTab tab)
        {
            var missing = new List<string>();
            var keys = tab.For(kind)?.For(platform);
            if (keys == null) { missing.Add("provider"); return missing; }
            if (string.IsNullOrWhiteSpace(keys.AppKey)) missing.Add(kind == AdProviderKind.AdMob ? "App ID" : "App Key");
            if (Uses(AdFormat.Rewarded) && string.IsNullOrWhiteSpace(keys.RewardedUnitId)) missing.Add("rewarded unit id");
            if (Uses(AdFormat.Interstitial) && string.IsNullOrWhiteSpace(keys.InterstitialUnitId)) missing.Add("interstitial unit id");
            if (Uses(AdFormat.Banner) && string.IsNullOrWhiteSpace(keys.BannerUnitId)) missing.Add("banner unit id");
            return missing;
        }

        /// <summary>The provider configuration a build on a platform runs with; the Test tab carries test mode and the device list.</summary>
        public ProviderConfig ToProviderConfig(BuildKind build, AdPlatform platform, AdProviderKind kind)
        {
            bool test = !UsesReleaseTab(build);
            var keys = TabFor(build).For(kind)?.For(platform) ?? new PlatformKeys();
            var units = new Dictionary<AdFormat, string>();
            foreach (AdFormat format in Enum.GetValues(typeof(AdFormat)))
                if (!string.IsNullOrWhiteSpace(keys.UnitId(format))) units[format] = keys.UnitId(format).Trim();
            bool published = kind == AdProviderKind.AdMob && AdMobSampleIds.IsSample(keys.AppKey);
            return new ProviderConfig(keys.AppKey?.Trim(), units,
                testMode: test && kind != AdProviderKind.AdMob,
                testDeviceIds: test ? TestDeviceIds : null,
                usesPublishedTestData: test && published,
                childDirected: ChildDirected, underAgeOfConsent: UnderAgeOfConsent, maxAdContentRating: MaxAdContentRating.ToString());
        }

        public AdsConfig ToConfig(BuildKind build, AdPlatform platform)
            => new AdsConfig(ToPlacements(), Policy.ToPolicy(), ToProviderConfig(build, platform, SelectedMediation), build);
    }

    /// <summary>Google's published sample publisher; the single runtime copy of the catalog's <c>publisherPrefix</c> (an EditMode test keeps them equal).</summary>
    public static class AdMobSampleIds
    {
        public const string PublisherPrefix = "ca-app-pub-3940256099942544";
        public static bool IsSample(string id) => !string.IsNullOrEmpty(id) && id.Trim().StartsWith(PublisherPrefix, StringComparison.Ordinal);
    }
}
