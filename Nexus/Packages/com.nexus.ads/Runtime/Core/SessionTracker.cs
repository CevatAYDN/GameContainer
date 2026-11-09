using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Ads
{
    /// <summary>
    /// The pacing memory: when the session started and its counts, which start over with each session, and
    /// what showed when and the per-day counters, which survive a restart through <see cref="IPacingStore"/>
    /// (issue #153), as does the count of sessions started on this device (issue #128). A session starts cold or when the
    /// app returns after <see cref="AdPolicy.SessionResumeGapMinutes"/> in the background.
    /// </summary>
    public sealed class SessionTracker
    {
        private const string DayKeyPrefix = "nexus.ads.day.";
        private const string DayTotalKey = "nexus.ads.interstitials.day";
        private const string LegacyDayTotalKey = "nexus.ads.day.interstitials";
        private const string LastInterstitialKey = "nexus.ads.last.interstitial";
        private const string LastRewardedKey = "nexus.ads.last.rewarded";
        private const string LastPlacementPrefix = "nexus.ads.last.placement.";
        private const string LifetimeSessionsKey = "nexus.ads.sessions.lifetime";

        private readonly IAdClock _clock;
        private readonly IPacingStore _store;
        private readonly Dictionary<string, int> _sessionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // on this run's clock; an earlier run's show is negative, a placement never shown is -infinity; read from the store once
        private readonly Dictionary<string, double> _lastShownAt = new Dictionary<string, double>(StringComparer.Ordinal);
        private double _hiddenAt = double.NaN;

        public double SessionStartedAt { get; private set; }
        public int InterstitialsThisSession { get; private set; }
        /// <summary>The last interstitial close on <see cref="IAdClock.Seconds"/> (an earlier run's is negative), or NaN.</summary>
        public double LastInterstitialAt { get; private set; }
        /// <summary>The last rewarded close on <see cref="IAdClock.Seconds"/> (an earlier run's is negative), or NaN.</summary>
        public double LastRewardedAt { get; private set; }
        /// <summary>
        /// Sessions started on this device before the current one, a resume after the gap included. Kept in the
        /// <see cref="IPacingStore"/> like the day counters (issue #128 asked for the core's player-prefs service, which this
        /// engine-free package cannot reference); a player who had the game before this counter existed starts at 0.
        /// </summary>
        public int SessionsBefore { get; private set; }

        public SessionTracker(IAdClock clock, IPacingStore store)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            // Separate the global row from placement names. Even zero marks the migration complete,
            // so a later "interstitials" placement cannot be imported as a global count on restart.
            if (_store.Get(DayTotalKey) == null)
                _store.Set(DayTotalKey, _store.Get(LegacyDayTotalKey) ?? _clock.UtcDay.ToString(CultureInfo.InvariantCulture) + ":0");
            StartSession();
            LastInterstitialAt = ReadLastShown(LastInterstitialKey);
            LastRewardedAt = ReadLastShown(LastRewardedKey);
        }

        public double SecondsSinceSessionStart => _clock.Seconds - SessionStartedAt;

        public int SessionCount(string placement) => _sessionCounts.TryGetValue(placement, out var n) ? n : 0;

        public double SecondsSinceShown(string placement)
        {
            if (!_lastShownAt.TryGetValue(placement, out var at))
            {
                at = ReadLastShown(LastPlacementPrefix + placement);
                if (double.IsNaN(at)) at = double.NegativeInfinity;
                _lastShownAt[placement] = at;
            }
            return _clock.Seconds - at;
        }

        public int DayCount(string placement) => ReadDay(DayKeyPrefix + placement);

        public int InterstitialsToday => ReadDay(DayTotalKey);

        /// <summary>Records a show that reached the screen (any result but NotReady, Blocked or Failed).</summary>
        public void RecordShown(AdPlacement placement)
        {
            double now = _clock.Seconds;
            string utc = _clock.UtcSeconds.ToString("R", CultureInfo.InvariantCulture);
            _sessionCounts[placement.Name] = SessionCount(placement.Name) + 1;
            _lastShownAt[placement.Name] = now;
            _store.Set(LastPlacementPrefix + placement.Name, utc);
            IncrementDay(DayKeyPrefix + placement.Name);
            if (placement.Format == AdFormat.Interstitial)
            {
                InterstitialsThisSession++;
                LastInterstitialAt = now;
                _store.Set(LastInterstitialKey, utc);
                IncrementDay(DayTotalKey);
            }
            else if (placement.Format == AdFormat.Rewarded)
            {
                LastRewardedAt = now;
                _store.Set(LastRewardedKey, utc);
            }
        }

        /// <summary>Call on every visibility change; a return after the resume gap starts a new session.</summary>
        public void OnVisibilityChanged(bool visible, AdPolicy policy)
        {
            if (!visible)
            {
                _hiddenAt = _clock.Seconds;
                return;
            }
            if (!double.IsNaN(_hiddenAt) && _clock.Seconds - _hiddenAt >= policy.SessionResumeGapMinutes * 60) StartSession();
            _hiddenAt = double.NaN;
        }

        // The gaps and cooldowns run from the show, not from the session: a resume keeps them as a restart does.
        private void StartSession()
        {
            SessionStartedAt = _clock.Seconds;
            InterstitialsThisSession = 0;
            _sessionCounts.Clear();
            string raw = _store.Get(LifetimeSessionsKey);
            SessionsBefore = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int started) && started > 0 ? started : 0;
            _store.Set(LifetimeSessionsKey, (SessionsBefore == int.MaxValue ? SessionsBefore : SessionsBefore + 1).ToString(CultureInfo.InvariantCulture));
        }

        // A show of an earlier run is stored as UTC seconds and placed on this run's clock; one ahead of the wall clock (the
        // device clock was set back) counts as just shown. NaN when the row is missing or unreadable.
        private double ReadLastShown(string key)
        {
            string raw = _store.Get(key);
            if (string.IsNullOrEmpty(raw) || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double utc)) return double.NaN;
            return _clock.Seconds - Math.Max(0, _clock.UtcSeconds - utc);
        }

        // Day counters are stored as "<day>:<count>"; a row from an earlier day counts as zero. A row from a later day than the clock's
        // (the device clock was set back) keeps counting, or setting the clock back lifted every day cap (issue #195); a clock set
        // forward cannot be told from a real day change without a server's time, which the package does not read.
        private int ReadDay(string key) => ReadDay(key, out _);

        private int ReadDay(string key, out long day)
        {
            day = _clock.UtcDay;
            string raw = _store.Get(key);
            if (string.IsNullOrEmpty(raw)) return 0;
            int colon = raw.IndexOf(':');
            if (colon <= 0) return 0;
            if (!long.TryParse(raw.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out long storedDay)) return 0;
            if (storedDay < day) return 0;
            day = storedDay;
            return int.TryParse(raw.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
        }

        private void IncrementDay(string key)
        {
            int count = ReadDay(key, out long day);
            _store.Set(key, day.ToString(CultureInfo.InvariantCulture) + ":" + (count + 1).ToString(CultureInfo.InvariantCulture));
        }
    }
}
