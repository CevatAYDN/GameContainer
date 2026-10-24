using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Ads
{
    /// <summary>
    /// The pacing memory of one process: when the session started, what showed when, and the per-day
    /// counters that survive a restart through <see cref="IPacingStore"/>. A session starts cold or
    /// when the app returns after <see cref="AdPolicy.SessionResumeGapMinutes"/> in the background.
    /// </summary>
    public sealed class SessionTracker
    {
        private const string DayKeyPrefix = "nexus.ads.day.";
        private const string DayTotalKey = "nexus.ads.day.interstitials";

        private readonly IAdClock _clock;
        private readonly IPacingStore _store;
        private readonly Dictionary<string, int> _sessionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _lastShownAt = new Dictionary<string, double>(StringComparer.Ordinal);
        private double _hiddenAt = double.NaN;

        public double SessionStartedAt { get; private set; }
        public int InterstitialsThisSession { get; private set; }
        /// <summary>Seconds of the last interstitial close, or NaN.</summary>
        public double LastInterstitialAt { get; private set; } = double.NaN;
        /// <summary>Seconds of the last rewarded close, or NaN.</summary>
        public double LastRewardedAt { get; private set; } = double.NaN;

        public SessionTracker(IAdClock clock, IPacingStore store)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            StartSession();
        }

        public double SecondsSinceSessionStart => _clock.Seconds - SessionStartedAt;

        public int SessionCount(string placement) => _sessionCounts.TryGetValue(placement, out var n) ? n : 0;

        public double SecondsSinceShown(string placement)
            => _lastShownAt.TryGetValue(placement, out var at) ? _clock.Seconds - at : double.PositiveInfinity;

        public int DayCount(string placement) => ReadDay(DayKeyPrefix + placement);

        public int InterstitialsToday => ReadDay(DayTotalKey);

        /// <summary>Records a show that reached the screen (any result but NotReady, Blocked or Failed).</summary>
        public void RecordShown(AdPlacement placement)
        {
            double now = _clock.Seconds;
            _sessionCounts[placement.Name] = SessionCount(placement.Name) + 1;
            _lastShownAt[placement.Name] = now;
            WriteDay(DayKeyPrefix + placement.Name, DayCount(placement.Name) + 1);
            if (placement.Format == AdFormat.Interstitial)
            {
                InterstitialsThisSession++;
                LastInterstitialAt = now;
                WriteDay(DayTotalKey, InterstitialsToday + 1);
            }
            else if (placement.Format == AdFormat.Rewarded)
            {
                LastRewardedAt = now;
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

        private void StartSession()
        {
            SessionStartedAt = _clock.Seconds;
            InterstitialsThisSession = 0;
            LastInterstitialAt = double.NaN;
            LastRewardedAt = double.NaN;
            _sessionCounts.Clear();
            _lastShownAt.Clear();
        }

        // Day counters are stored as "<day>:<count>"; a row from another day counts as zero.
        private int ReadDay(string key)
        {
            string raw = _store.Get(key);
            if (string.IsNullOrEmpty(raw)) return 0;
            int colon = raw.IndexOf(':');
            if (colon <= 0) return 0;
            if (!long.TryParse(raw.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out long day)) return 0;
            if (day != _clock.UtcDay) return 0;
            return int.TryParse(raw.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
        }

        private void WriteDay(string key, int count)
            => _store.Set(key, _clock.UtcDay.ToString(CultureInfo.InvariantCulture) + ":" + count.ToString(CultureInfo.InvariantCulture));
    }
}
