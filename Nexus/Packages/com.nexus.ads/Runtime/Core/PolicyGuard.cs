using System;
using System.Globalization;

namespace Nexus.Ads
{
    /// <summary>What the controller knows at the moment of a check and the guard cannot see itself.</summary>
    public readonly struct GuardState
    {
        public readonly bool FullScreenOpen;
        public readonly bool ConsentPending;

        public GuardState(bool fullScreenOpen, bool consentPending)
        {
            FullScreenOpen = fullScreenOpen;
            ConsentPending = consentPending;
        }
    }

    /// <summary>
    /// Answers "may this placement show now?" from the placement table, the policy floors, the session
    /// and visibility. It never shows anything and raises nothing; the controller turns a block into a
    /// <see cref="PolicyViolation"/> event. The rule ids are <see cref="PolicyRules"/>; the policies behind
    /// them are in <c>docs/POLICY_COMPLIANCE.md</c>.
    /// </summary>
    public sealed class PolicyGuard
    {
        private readonly AdsConfig _config;
        private readonly IAppVisibility _visibility;
        private readonly SessionTracker _session;

        public AdPolicy Policy { get; private set; }

        public PolicyGuard(AdsConfig config, AdPolicy policy, IAppVisibility visibility, SessionTracker session)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>Replaces the floors with a stricter set; a looser set is ignored (see <see cref="AdPolicy.Tighten(AdPolicy)"/>).</summary>
        public void Tighten(AdPolicy requested) => Policy = Policy.Tighten(requested);

        public void Tighten(string key, double value) => Policy = Policy.Tighten(key, value);

        public PolicyVerdict Check(string placementName, AdFormat format, GuardState state)
        {
            var placement = _config.Find(placementName);
            if (placement == null)
                return PolicyVerdict.Block(PolicyRules.UnknownPlacement, "placement '" + placementName + "' is not declared in the settings");
            if (placement.Format != format)
                return PolicyVerdict.Block(PolicyRules.UnknownPlacement, "placement '" + placementName + "' is declared as " + placement.Format + ", not " + format);
            if (state.ConsentPending)
                return PolicyVerdict.Block(PolicyRules.NoRequestBeforeConsent, "the consent flow has not finished");
            if (!_visibility.Visible)
                return PolicyVerdict.Block(PolicyRules.NoAdInBackground, "the app is not visible");
            if (state.FullScreenOpen && format != AdFormat.Banner)
                return PolicyVerdict.Block(PolicyRules.NoStackedAds, "a full-screen ad is open");
            return format == AdFormat.Interstitial ? CheckInterstitial(placement) : CheckPlacementPacing(placement);
        }

        private PolicyVerdict CheckInterstitial(AdPlacement placement)
        {
            if (!placement.NaturalBreak)
                return PolicyVerdict.Block(PolicyRules.InterstitialNaturalBreakOnly, "placement '" + placement.Name + "' is not marked NaturalBreak");
            double sinceStart = _session.SecondsSinceSessionStart;
            if (sinceStart < Policy.NoInterstitialForFirstSeconds)
                return Frequency("the session is " + Seconds(sinceStart) + " old; the first interstitial waits " + Seconds(Policy.NoInterstitialForFirstSeconds));
            if (!double.IsNaN(_session.LastInterstitialAt))
            {
                double gap = _session.SecondsSinceSessionStart - (_session.LastInterstitialAt - _session.SessionStartedAt);
                if (gap < Policy.MinInterstitialGapSeconds)
                    return Frequency("the last interstitial was " + Seconds(gap) + " ago; the gap is " + Seconds(Policy.MinInterstitialGapSeconds));
            }
            if (!double.IsNaN(_session.LastRewardedAt))
            {
                double sinceRewarded = _session.SecondsSinceSessionStart - (_session.LastRewardedAt - _session.SessionStartedAt);
                if (sinceRewarded < Policy.NoInterstitialAfterRewardedSeconds)
                    return Frequency("a rewarded ad closed " + Seconds(sinceRewarded) + " ago; interstitials wait " + Seconds(Policy.NoInterstitialAfterRewardedSeconds));
            }
            if (Policy.MaxInterstitialsPerSession > 0 && _session.InterstitialsThisSession >= Policy.MaxInterstitialsPerSession)
                return Frequency("the session cap of " + Policy.MaxInterstitialsPerSession + " interstitials is reached");
            if (Policy.MaxInterstitialsPerDay > 0 && _session.InterstitialsToday >= Policy.MaxInterstitialsPerDay)
                return Frequency("the day cap of " + Policy.MaxInterstitialsPerDay + " interstitials is reached");
            return CheckPlacementPacing(placement);
        }

        // Per-placement cooldown and caps apply to every format; they can only add restrictions.
        private PolicyVerdict CheckPlacementPacing(AdPlacement placement)
        {
            if (placement.FirstShowAfterSeconds > 0 && _session.SecondsSinceSessionStart < placement.FirstShowAfterSeconds)
                return Frequency("placement '" + placement.Name + "' waits " + Seconds(placement.FirstShowAfterSeconds) + " after session start");
            if (placement.CooldownSeconds > 0 && _session.SecondsSinceShown(placement.Name) < placement.CooldownSeconds)
                return Frequency("placement '" + placement.Name + "' showed " + Seconds(_session.SecondsSinceShown(placement.Name)) + " ago; its cooldown is " + Seconds(placement.CooldownSeconds));
            if (placement.SessionCap > 0 && _session.SessionCount(placement.Name) >= placement.SessionCap)
                return Frequency("placement '" + placement.Name + "' reached its session cap of " + placement.SessionCap);
            if (placement.DayCap > 0 && _session.DayCount(placement.Name) >= placement.DayCap)
                return Frequency("placement '" + placement.Name + "' reached its day cap of " + placement.DayCap);
            return PolicyVerdict.Ok;
        }

        private static PolicyVerdict Frequency(string reason) => PolicyVerdict.Block(PolicyRules.InterstitialFrequency, reason);

        private static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }
}
