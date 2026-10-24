namespace Nexus.Ads
{
    /// <summary>
    /// The rule identifiers of <see cref="PolicyGuard"/>. Each one maps to the platform policies in
    /// <c>docs/POLICY_COMPLIANCE.md</c>; code, tests and <see cref="PolicyViolation.Rule"/> use these
    /// exact strings. Rules marked "structural" have no runtime check because the API offers no way
    /// to break them; the tests prove the shape instead.
    /// </summary>
    public static class PolicyRules
    {
        /// <summary>Structural: the only show path is <see cref="AdController.Show"/> and the reward is granted once, only on the SDK's reward callback.</summary>
        public const string RewardedOnRequestOnly = "RewardedOnRequestOnly";
        /// <summary>An interstitial on a placement without <see cref="AdPlacement.NaturalBreak"/>.</summary>
        public const string InterstitialNaturalBreakOnly = "InterstitialNaturalBreakOnly";
        /// <summary>Minimum gap, first-show delay, per-session cap, per-day cap, placement cooldown, gap after a rewarded ad.</summary>
        public const string InterstitialFrequency = "InterstitialFrequency";
        /// <summary>A second full-screen ad while one is open.</summary>
        public const string NoStackedAds = "NoStackedAds";
        /// <summary>A show while the app is not visible.</summary>
        public const string NoAdInBackground = "NoAdInBackground";
        /// <summary>A show or load before the consent flow finished.</summary>
        public const string NoRequestBeforeConsent = "NoRequestBeforeConsent";
        /// <summary>Structural: <see cref="ProviderConfig"/> always carries the child-directed flag, the under-age flag and the content rating to <see cref="IAdProvider.Initialize"/>.</summary>
        public const string ChildSettingsForwarded = "ChildSettingsForwarded";
        /// <summary>A second mediation provider started in the same session.</summary>
        public const string SingleMediation = "SingleMediation";
        /// <summary>Structural: loads come only from the controller's preload and <see cref="RetryPolicy"/>.</summary>
        public const string NoAutomaticRequests = "NoAutomaticRequests";
        /// <summary>Structural: the core has no API that touches an SDK ad view.</summary>
        public const string NoAdViewInterference = "NoAdViewInterference";
        /// <summary>A development build without test mode or published test units; a release build with them.</summary>
        public const string TestModeRequired = "TestModeRequired";
        /// <summary>A development build on a device outside the test device list: the simulator runs instead of the SDK.</summary>
        public const string UnregisteredDevice = "UnregisteredDevice";
        /// <summary>A placement name that is not declared, or declared with another format.</summary>
        public const string UnknownPlacement = "UnknownPlacement";
    }

    /// <summary>A blocked call. Raised as an event; the Unity layer logs it in development builds and reports it in release builds.</summary>
    public readonly struct PolicyViolation
    {
        public readonly string Rule;
        public readonly string Placement;
        public readonly AdFormat Format;
        public readonly string Reason;

        public PolicyViolation(string rule, string placement, AdFormat format, string reason)
        {
            Rule = rule;
            Placement = placement;
            Format = format;
            Reason = reason;
        }

        public override string ToString() => Rule + " (" + Format + " '" + Placement + "'): " + Reason;
    }

    /// <summary>The answer of <see cref="PolicyGuard.Check"/>.</summary>
    public readonly struct PolicyVerdict
    {
        public readonly bool Allowed;
        public readonly string Rule;
        public readonly string Reason;

        private PolicyVerdict(bool allowed, string rule, string reason)
        {
            Allowed = allowed;
            Rule = rule;
            Reason = reason;
        }

        public static readonly PolicyVerdict Ok = new PolicyVerdict(true, null, null);
        public static PolicyVerdict Block(string rule, string reason) => new PolicyVerdict(false, rule, reason);
    }
}
