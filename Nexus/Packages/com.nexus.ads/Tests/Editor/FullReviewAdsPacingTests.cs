using System;
using System.Collections.Generic;
using Nexus.Ads;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsPacingTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void A10_InterstitialNamedInterstitialsConsumesOnePlacementAndGlobalAllowance()
        {
            var clock = new Clock();
            var store = new Store();
            var session = new SessionTracker(clock, store);
            var placement = new AdPlacement("interstitials", AdFormat.Interstitial, naturalBreak: true, dayCap: 2);
            var guard = Guard(session, 2, placement);

            session.RecordShown(placement);

            Require(session.DayCount(placement.Name) == 1 && session.InterstitialsToday == 1,
                $"One show must count once in each independent counter; placement={session.DayCount(placement.Name)}, global={session.InterstitialsToday}.");
            Require(guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "A day cap of two must still allow the second interstitial.");
            session.RecordShown(placement);
            Require(!guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "The third interstitial must be blocked by the day cap.");

            var reopened = new SessionTracker(clock, store);
            Require(reopened.DayCount(placement.Name) == 2 && reopened.InterstitialsToday == 2,
                "Both independent counts must survive a new session over the same store.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A10_RewardedNamedInterstitialsDoesNotConsumeGlobalInterstitialAllowance()
        {
            var clock = new Clock();
            var store = new Store();
            var session = new SessionTracker(clock, store);
            var reward = new AdPlacement("interstitials", AdFormat.Rewarded);
            var interstitial = new AdPlacement("level_end", AdFormat.Interstitial, naturalBreak: true);

            session.RecordShown(reward);
            var reopened = new SessionTracker(clock, store);
            var guard = Guard(reopened, 1, reward, interstitial);

            Require(reopened.DayCount(reward.Name) == 1 && reopened.InterstitialsToday == 0,
                $"A reward must not become an interstitial on reopen; placement={reopened.DayCount(reward.Name)}, global={reopened.InterstitialsToday}.");
            Require(guard.Check(interstitial.Name, interstitial.Format, new GuardState(false, false)).Allowed,
                "The player has not consumed the first interstitial allowance.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A10_LegacyGlobalCountSurvivesUpgradeAndDoesNotResurrectAfterTheNextDay()
        {
            var clock = new Clock();
            var store = new Store();
            store.Set("nexus.ads.day.interstitials", "100:2");
            var placement = new AdPlacement("level_end", AdFormat.Interstitial, naturalBreak: true);
            var session = new SessionTracker(clock, store);
            var guard = Guard(session, 2, placement);

            Require(session.InterstitialsToday == 2 &&
                !guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "Separating the keys must not erase a legacy persisted global cap.");
            clock.UtcDay = 101;
            Require(session.InterstitialsToday == 0, "A genuine next day must release the old cap.");
            session.RecordShown(placement);

            var reopened = new SessionTracker(clock, store);
            Require(reopened.InterstitialsToday == 1 && reopened.DayCount(placement.Name) == 1,
                "A later session must retain today's count instead of importing the legacy row again.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A11_RollbackThenAnotherShowCannotResetTheOriginalGlobalDayCap()
        {
            var clock = new Clock();
            var store = new Store();
            var session = new SessionTracker(clock, store);
            var placement = new AdPlacement("level_end", AdFormat.Interstitial, naturalBreak: true);
            var guard = Guard(session, 2, placement);
            session.RecordShown(placement);
            clock.UtcDay = 99;
            Require(guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "Only one of the two allowed interstitials has been shown.");
            session.RecordShown(placement);

            clock.UtcDay = 100;
            var reopened = new SessionTracker(clock, store);
            var reopenedGuard = Guard(reopened, 2, placement);
            Require(reopened.InterstitialsToday == 2 &&
                !reopenedGuard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                $"Returning to the original day must retain both shows and block the third; global={reopened.InterstitialsToday}.");
            clock.UtcDay = 101;
            Require(reopenedGuard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "The next genuine UTC day must release the cap.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A11_RollbackThenAnotherRewardCannotResetItsPlacementDayCap()
        {
            var clock = new Clock();
            var store = new Store();
            var placement = new AdPlacement("boost", AdFormat.Rewarded, dayCap: 2);
            var session = new SessionTracker(clock, store);
            session.RecordShown(placement);
            clock.UtcDay = 99;
            session.RecordShown(placement);

            clock.UtcDay = 100;
            var reopened = new SessionTracker(clock, store);
            var guard = Guard(reopened, 0, placement);
            Require(reopened.DayCount(placement.Name) == 2 &&
                !guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                $"A placement's day cap must retain the rollback-period show; placement={reopened.DayCount(placement.Name)}.");
            clock.UtcDay = 101;
            Require(guard.Check(placement.Name, placement.Format, new GuardState(false, false)).Allowed,
                "A genuine new day must still release the placement cap.");
        }

        private static PolicyGuard Guard(SessionTracker session, int globalDayCap, params AdPlacement[] placements)
        {
            var policy = new AdPolicy(0, 0, 0, 0, globalDayCap, 30);
            var config = new AdsConfig(placements, policy,
                new ProviderConfig("", null, false, null, false, false, false, "G"), BuildKind.Editor);
            return new PolicyGuard(config, policy, new Visibility(), session);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Clock : IAdClock
        {
            public double Seconds { get; set; } = 1000;
            public long UtcDay { get; set; } = 100;
            public double UtcSeconds => UtcDay * 86400 + Seconds;
        }

        private sealed class Visibility : IAppVisibility
        {
            public bool Visible => true;
            public event Action<bool> Changed { add { } remove { } }
        }

        private sealed class Store : IPacingStore
        {
            private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();
            public string Get(string key) => _rows.TryGetValue(key, out string value) ? value : null;
            public void Set(string key, string value) => _rows[key] = value;
        }
    }
}
