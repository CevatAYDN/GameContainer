namespace Nexus.Ads
{
    /// <summary>The ad formats the core knows. App-open and native are out of scope.</summary>
    public enum AdFormat
    {
        Rewarded,
        Interstitial,
        Banner,
    }

    /// <summary>Where a banner is anchored.</summary>
    public enum BannerPosition
    {
        Bottom,
        Top,
    }

    /// <summary>What a show attempt ended with.</summary>
    public enum AdResult
    {
        /// <summary>The SDK's reward callback fired. Only this value pays the player.</summary>
        Rewarded,
        /// <summary>The player closed the ad before the reward, or an interstitial closed.</summary>
        Closed,
        /// <summary>Nothing was loaded, the kill switch is on, the player is ad-free, or consent was refused; nothing was shown.</summary>
        NotReady,
        /// <summary>The SDK failed to show a loaded ad.</summary>
        Failed,
        /// <summary>A <see cref="PolicyGuard"/> rule blocked the show; a <see cref="PolicyViolation"/> was raised.</summary>
        Blocked,
    }

    /// <summary>How the build was made; the Unity layer answers it, the core only reads it.</summary>
    public enum BuildKind
    {
        Editor,
        Development,
        Release,
    }
}
