using System;

namespace Nexus.Ads
{
    /// <summary>
    /// The frequency floors <see cref="PolicyGuard"/> enforces. Immutable; every change goes through
    /// <see cref="Tighten(string,double)"/>, which accepts a value only when it is stricter than the
    /// current one, so neither the editor, a game nor remote configuration can loosen a floor.
    /// The defaults are the package's safe values, not platform numbers; the platform texts are in
    /// <c>docs/POLICY_COMPLIANCE.md</c>.
    /// </summary>
    public sealed class AdPolicy
    {
        /// <summary>Keys accepted by <see cref="Tighten(string,double)"/>; also the remote-configuration keys.</summary>
        public static class Keys
        {
            public const string MinInterstitialGapSeconds = "ads.min_interstitial_gap_seconds";
            public const string NoInterstitialForFirstSeconds = "ads.no_interstitial_for_first_seconds";
            public const string NoInterstitialAfterRewardedSeconds = "ads.no_interstitial_after_rewarded_seconds";
            public const string MaxInterstitialsPerSession = "ads.max_interstitials_per_session";
            public const string MaxInterstitialsPerDay = "ads.max_interstitials_per_day";
            public const string SessionResumeGapMinutes = "ads.session_resume_gap_minutes";

            public static readonly string[] All =
            {
                MinInterstitialGapSeconds, NoInterstitialForFirstSeconds, NoInterstitialAfterRewardedSeconds,
                MaxInterstitialsPerSession, MaxInterstitialsPerDay, SessionResumeGapMinutes,
            };
        }

        public static readonly AdPolicy Default = new AdPolicy(30, 60, 30, 0, 0, 30);

        /// <summary>Seconds between two interstitials, any placement.</summary>
        public double MinInterstitialGapSeconds { get; }
        /// <summary>Seconds after session start without an interstitial.</summary>
        public double NoInterstitialForFirstSeconds { get; }
        /// <summary>Seconds after a rewarded ad without an interstitial.</summary>
        public double NoInterstitialAfterRewardedSeconds { get; }
        /// <summary>Interstitials per session, all placements; 0 = no cap.</summary>
        public int MaxInterstitialsPerSession { get; }
        /// <summary>Interstitials per UTC day, all placements; 0 = no cap.</summary>
        public int MaxInterstitialsPerDay { get; }
        /// <summary>A return from the background after this many minutes starts a new session.</summary>
        public double SessionResumeGapMinutes { get; }

        public AdPolicy(double minInterstitialGapSeconds, double noInterstitialForFirstSeconds, double noInterstitialAfterRewardedSeconds,
            int maxInterstitialsPerSession, int maxInterstitialsPerDay, double sessionResumeGapMinutes)
        {
            if (minInterstitialGapSeconds < 0 || noInterstitialForFirstSeconds < 0 || noInterstitialAfterRewardedSeconds < 0
                || maxInterstitialsPerSession < 0 || maxInterstitialsPerDay < 0 || sessionResumeGapMinutes < 0)
                throw new ArgumentOutOfRangeException(nameof(minInterstitialGapSeconds), "Policy values cannot be negative.");
            MinInterstitialGapSeconds = minInterstitialGapSeconds;
            NoInterstitialForFirstSeconds = noInterstitialForFirstSeconds;
            NoInterstitialAfterRewardedSeconds = noInterstitialAfterRewardedSeconds;
            MaxInterstitialsPerSession = maxInterstitialsPerSession;
            MaxInterstitialsPerDay = maxInterstitialsPerDay;
            SessionResumeGapMinutes = sessionResumeGapMinutes;
        }

        /// <summary>
        /// Returns a policy with <paramref name="key"/> set to <paramref name="value"/> when that is stricter
        /// (gaps and delays only grow, caps only shrink, a cap of 0 never replaces a cap), otherwise this policy.
        /// An unknown key returns this policy.
        /// </summary>
        public AdPolicy Tighten(string key, double value)
        {
            if (value < 0) return this;
            switch (key)
            {
                case Keys.MinInterstitialGapSeconds:
                    return value > MinInterstitialGapSeconds ? new AdPolicy(value, NoInterstitialForFirstSeconds, NoInterstitialAfterRewardedSeconds, MaxInterstitialsPerSession, MaxInterstitialsPerDay, SessionResumeGapMinutes) : this;
                case Keys.NoInterstitialForFirstSeconds:
                    return value > NoInterstitialForFirstSeconds ? new AdPolicy(MinInterstitialGapSeconds, value, NoInterstitialAfterRewardedSeconds, MaxInterstitialsPerSession, MaxInterstitialsPerDay, SessionResumeGapMinutes) : this;
                case Keys.NoInterstitialAfterRewardedSeconds:
                    return value > NoInterstitialAfterRewardedSeconds ? new AdPolicy(MinInterstitialGapSeconds, NoInterstitialForFirstSeconds, value, MaxInterstitialsPerSession, MaxInterstitialsPerDay, SessionResumeGapMinutes) : this;
                case Keys.MaxInterstitialsPerSession:
                    return StricterCap(MaxInterstitialsPerSession, value) ? new AdPolicy(MinInterstitialGapSeconds, NoInterstitialForFirstSeconds, NoInterstitialAfterRewardedSeconds, (int)value, MaxInterstitialsPerDay, SessionResumeGapMinutes) : this;
                case Keys.MaxInterstitialsPerDay:
                    return StricterCap(MaxInterstitialsPerDay, value) ? new AdPolicy(MinInterstitialGapSeconds, NoInterstitialForFirstSeconds, NoInterstitialAfterRewardedSeconds, MaxInterstitialsPerSession, (int)value, SessionResumeGapMinutes) : this;
                case Keys.SessionResumeGapMinutes:
                    return value > SessionResumeGapMinutes ? new AdPolicy(MinInterstitialGapSeconds, NoInterstitialForFirstSeconds, NoInterstitialAfterRewardedSeconds, MaxInterstitialsPerSession, MaxInterstitialsPerDay, value) : this;
                default:
                    return this;
            }
        }

        /// <summary>Applies every value of <paramref name="requested"/> through <see cref="Tighten(string,double)"/>.</summary>
        public AdPolicy Tighten(AdPolicy requested)
        {
            if (requested == null) return this;
            return Tighten(Keys.MinInterstitialGapSeconds, requested.MinInterstitialGapSeconds)
                .Tighten(Keys.NoInterstitialForFirstSeconds, requested.NoInterstitialForFirstSeconds)
                .Tighten(Keys.NoInterstitialAfterRewardedSeconds, requested.NoInterstitialAfterRewardedSeconds)
                .Tighten(Keys.MaxInterstitialsPerSession, requested.MaxInterstitialsPerSession)
                .Tighten(Keys.MaxInterstitialsPerDay, requested.MaxInterstitialsPerDay)
                .Tighten(Keys.SessionResumeGapMinutes, requested.SessionResumeGapMinutes);
        }

        // A cap of 0 means none; a positive cap is stricter than none and a smaller positive cap is stricter still.
        private static bool StricterCap(int current, double requested)
        {
            int cap = (int)requested;
            if (cap <= 0) return false;
            return current == 0 || cap < current;
        }
    }
}
