using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Ads;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsControllerTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void A03_Robustness_DuplicateResultAfterClosePreservesOneReceiptWithoutASecondCompletion()
        {
            using var rig = new Rig();
            var results = new List<AdResult>();
            rig.Controller.Show("first", results.Add);
            rig.Provider.Results[0](AdResult.Closed);
            Require(results.SequenceEqual(new[] { AdResult.Closed }) && !rig.Controller.PendingReward.HasValue,
                "An ordinary close must complete once without inventing a reward.");

            rig.Provider.Results[0](AdResult.Rewarded);

            Require(rig.Controller.PendingReward?.Placement == "first",
                "A late reward after an ordinary close must be recoverable.");
            string id = rig.Controller.PendingReward.Value.Id;
            rig.Provider.Results[0](AdResult.Rewarded);
            Require(rig.Controller.PendingReward?.Id == id, "A duplicate reward must not create another receipt.");
            rig.Controller.AcknowledgeReward(id);
            rig.Provider.Results[0](AdResult.Rewarded);
            Require(!rig.Controller.PendingReward.HasValue && results.Count == 1,
                "An acknowledged reward must not reappear or complete the game callback again.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A03_Robustness_RepeatedResultsAfterWatchdogCannotFinishTheNextShow()
        {
            using var rig = new Rig();
            var first = new List<AdResult>();
            var second = new List<AdResult>();
            rig.Controller.Show("first", first.Add);
            rig.Scheduler.Advance(AdController.ShowAnswerSeconds + 1);
            Require(first.SequenceEqual(new[] { AdResult.Failed }), "The first show must expire through the real watchdog.");
            rig.Controller.Show("second", second.Add);

            rig.Provider.Results[0](AdResult.Rewarded);
            rig.Provider.Results[0](AdResult.Closed);

            Require(rig.Controller.PendingReward?.Placement == "first" && second.Count == 0,
                "The old callback must retain its receipt identity without completing the next show.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            rig.Provider.Results[0](AdResult.Rewarded);
            Require(!rig.Controller.PendingReward.HasValue, "A repeated late reward must not be paid twice.");
            rig.Provider.Results[1](AdResult.Closed);
            Require(second.SequenceEqual(new[] { AdResult.Closed }), "Only the second show's own callback may finish it.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A06_DisposedControllerIgnoresLateShowResultsAndDoesNotReload()
        {
            using var rig = new Rig();
            int completions = 0, closed = 0;
            rig.Controller.AdClosed += (_, __) => closed++;
            rig.Controller.Show("first", _ => completions++);
            int loads = rig.Provider.Loads;
            rig.Controller.Dispose();

            rig.Provider.Results[0](AdResult.Rewarded);
            rig.Scheduler.Advance(100);

            Require(!rig.Controller.PendingReward.HasValue && completions == 0 && closed == 0,
                "A disposed controller must not deliver callbacks or create new receipts.");
            Require(rig.Provider.Loads == loads && rig.Controller.State == AdController.Phase.Stopped,
                "Late results must not reload or revive a disposed controller.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A06_ControllerLeavesBorrowedConsentAndProviderWithTheirOwners()
        {
            var consent = new Consent();
            using var rig = new Rig(consent);
            rig.Controller.Dispose();
            rig.Controller.Dispose();

            Require(consent.Disposals == 0 && rig.Provider.Disposals == 0,
                "The controller borrows its ports; their owners must decide when to dispose them.");
            Require(MediationLock.Holder == null && rig.Controller.State == AdController.Phase.Stopped,
                "Controller teardown must release its own state and mediation lock.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A03_EarnedNotificationAfterCloseRecoversOnceWithoutAnotherPublicResult()
        {
            var provider = new NotifyingProvider();
            using var rig = new Rig(provider: provider);
            var results = new List<AdResult>();
            rig.Controller.Show("first", results.Add);
            Require(provider.Earned.Count == 1, "The controller must bind the optional per-show earned notification.");
            provider.Results[0](AdResult.Closed);
            Require(!rig.Controller.PendingReward.HasValue, "A close without earning must not manufacture a reward.");

            provider.Earned[0]();

            Require(rig.Controller.PendingReward?.Placement == "first", "The earned notification must preserve the closed show's receipt.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            provider.Earned[0]();
            Require(!rig.Controller.PendingReward.HasValue && results.SequenceEqual(new[] { AdResult.Closed }),
                "Duplicate earning must not recreate an acknowledged receipt or invoke another public result.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A03_EarnedNotificationAfterWatchdogCannotCompleteTheNextShow()
        {
            var provider = new NotifyingProvider();
            using var rig = new Rig(provider: provider);
            var first = new List<AdResult>();
            var second = new List<AdResult>();
            rig.Controller.Show("first", first.Add);
            Require(provider.Earned.Count == 1, "The controller must bind the optional per-show earned notification.");
            rig.Scheduler.Advance(AdController.ShowAnswerSeconds + 1);
            rig.Controller.Show("second", second.Add);

            provider.Earned[0]();
            provider.Results[0](AdResult.Closed);

            Require(rig.Controller.PendingReward?.Placement == "first" && second.Count == 0 &&
                first.SequenceEqual(new[] { AdResult.Failed }),
                "The old show's earned notification and sole SDK close must not finish the replacement.");
            rig.Controller.AcknowledgeReward(rig.Controller.PendingReward.Value.Id);
            provider.Results[1](AdResult.Closed);
            Require(second.SequenceEqual(new[] { AdResult.Closed }), "The new show keeps its own completion.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A03_EarningBeforeWatchdogRemainsRecoverableWhenCloseNeverArrives()
        {
            var provider = new NotifyingProvider();
            using var rig = new Rig(provider: provider);
            var results = new List<AdResult>();
            rig.Controller.Show("first", results.Add);
            Require(provider.Earned.Count == 1, "The controller must bind the optional per-show earned notification.");
            provider.Earned[0]();
            Require(results.Count == 0, "Earning must not prematurely close the game's ad flow.");

            rig.Scheduler.Advance(AdController.ShowAnswerSeconds + 1);

            Require(results.SequenceEqual(new[] { AdResult.Failed }) && rig.Controller.PendingReward?.Placement == "first",
                "The watchdog still ends the unanswered show, but must retain its already-earned reward.");
            string id = rig.Controller.PendingReward.Value.Id;
            provider.Results[0](AdResult.Rewarded);
            Require(results.Count == 1 && rig.Controller.PendingReward?.Id == id,
                "The later sole provider result must not create another receipt.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A06_DisposedControllerIgnoresEarnedNotifications()
        {
            var provider = new NotifyingProvider();
            using var rig = new Rig(provider: provider);
            int completions = 0;
            rig.Controller.Show("first", _ => completions++);
            Require(provider.Earned.Count == 1, "The controller must bind the optional per-show earned notification.");
            rig.Controller.Dispose();

            provider.Earned[0]();
            provider.Results[0](AdResult.Rewarded);

            Require(!rig.Controller.PendingReward.HasValue && completions == 0 &&
                rig.Controller.State == AdController.Phase.Stopped,
                "A retired controller must not be revived through its earned notification or result.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Rig : IDisposable
        {
            public readonly Provider Provider;
            public readonly Scheduler Scheduler;
            public readonly AdController Controller;

            public Rig(IConsentFlow consent = null, Provider provider = null)
            {
                Provider = provider ?? new Provider();
                var clock = new Clock();
                Scheduler = new Scheduler(clock);
                var config = new AdsConfig(
                    new[] { new AdPlacement("first", AdFormat.Rewarded), new AdPlacement("second", AdFormat.Rewarded) },
                    AdPolicy.Default, new ProviderConfig("", null, false, null, false, false, false, "G"), BuildKind.Editor);
                Controller = new AdController(config, Provider, clock, new Visibility(), new MainThread(),
                    Scheduler, new Store(), new Telemetry(), consent);
                Controller.Start();
            }

            public void Dispose() => Controller.Dispose();
        }

        private class Provider : IAdProvider, IDisposable
        {
            private bool _ready;
            public readonly List<Action<AdResult>> Results = new List<Action<AdResult>>();
            public int Loads, Disposals;
            public string Name => "full-review";
            public event Action<AdFormat> Loaded;
            public event Action<AdFormat, string> LoadFailed { add { } remove { } }
            public event Action<AdFormat> Opened { add { } remove { } }
            public event Action<AdRevenue> Revenue { add { } remove { } }
            public void Initialize(ProviderConfig config, Action<bool> onReady) => onReady(true);
            public void Load(AdFormat format) { Loads++; _ready = true; Loaded?.Invoke(format); }
            public bool IsReady(AdFormat format) => _ready;
            public void Show(AdFormat format, string placement, Action<AdResult> result) { _ready = false; Results.Add(result); }
            public void ShowBanner(string placement, BannerPosition position) { }
            public void HideBanner() { }
            public void OpenTestSuite() { }
            public void Dispose() => Disposals++;
        }

        private sealed class NotifyingProvider : Provider, IRewardedShowProvider
        {
            public readonly List<Action> Earned = new List<Action>();
            void IRewardedShowProvider.ShowRewarded(string placement, Action<AdResult> result, Action earned)
            {
                Earned.Add(earned);
                Show(AdFormat.Rewarded, placement, result);
            }
        }

        private sealed class Consent : IConsentFlow, IDisposable
        {
            public int Disposals;
            public bool Completed => true;
            public bool CanRequestAds => true;
            public bool PrivacyOptionsRequired => false;
            public event Action Changed { add { } remove { } }
            public void Request(Action completed) => completed?.Invoke();
            public void ShowPrivacyOptions(Action closed) => closed?.Invoke();
            public void Dispose() => Disposals++;
        }

        private sealed class Clock : IAdClock
        {
            public double Seconds { get; set; } = 1000;
            public long UtcDay => 100;
            public double UtcSeconds => 8640000 + Seconds;
        }

        private sealed class Scheduler : IAdScheduler
        {
            private readonly Clock _clock;
            private readonly List<Entry> _pending = new List<Entry>();
            public Scheduler(Clock clock) => _clock = clock;
            public IDisposable Schedule(double seconds, Action action)
            {
                var entry = new Entry { At = _clock.Seconds + seconds, Action = action };
                _pending.Add(entry);
                return entry;
            }
            public void Advance(double seconds)
            {
                double end = _clock.Seconds + seconds;
                Entry next;
                while ((next = _pending.Where(item => !item.Done && item.At <= end).OrderBy(item => item.At).FirstOrDefault()) != null)
                {
                    next.Done = true;
                    _clock.Seconds = next.At;
                    next.Action();
                }
                _clock.Seconds = end;
            }
            private sealed class Entry : IDisposable
            {
                public double At;
                public Action Action;
                public bool Done;
                public void Dispose() => Done = true;
            }
        }

        private sealed class Visibility : IAppVisibility
        {
            public bool Visible => true;
            public event Action<bool> Changed { add { } remove { } }
        }
        private sealed class MainThread : IMainThread
        {
            public bool IsCurrent => true;
            public void Post(Action action) => action();
        }
        private sealed class Store : IPacingStore
        {
            private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();
            public string Get(string key) => _rows.TryGetValue(key, out string value) ? value : null;
            public void Set(string key, string value) => _rows[key] = value;
        }
        private sealed class Telemetry : IAdTelemetry { public void Record(in AdEvent e) { } }
    }
}
