using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Ads;
using Nexus.Ads.Bridge;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewAdsAwaitContractTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void Context_ServiceAndAdapterPreserveTokenInsideDeliveryBeforeAck()
        {
            using var rig = new Rig();
            string delivered = null;
            IAdService service = rig.Service;
            service.RequestReward("reward", "saved-offer-630", outcome =>
            {
                Require(outcome == AdOutcome.Rewarded, "Expected rewarded outcome.");
                delivered = rig.Controller.DeliveringReward.Value.Context;
                Require(rig.Controller.PendingReward?.Context == delivered, "Receipt context must persist before delivery.");
            });
            rig.Provider.CompleteShow(AdResult.Rewarded);
            Require(delivered == "saved-offer-630" && !rig.Controller.PendingReward.HasValue,
                "The entire IAdService -> adapter -> controller route must keep context and acknowledge its receipt.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task Context_GrantAsyncFailureRetainsTokenForReplay()
        {
            using var rig = new Rig();
            var pending = rig.Service.RequestRewardAsync("reward", "offer-800", () => throw new System.IO.IOException("save failed"));
            try { rig.Provider.CompleteShow(AdResult.Rewarded); }
            catch (System.IO.IOException) { }
            bool failed = false;
            try { await pending; }
            catch (System.IO.IOException) { failed = true; }
            Require(failed && rig.Controller.PendingReward?.Context == "offer-800", "Failure must preserve this offer, not return Rewarded or discard context.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Context_UnsupportedAdapterRejectsBeforeShowingAndLegacyStillWorks()
        {
            using var service = new AdService();
            var adapter = new LegacyAdapter();
            service.SetNetworkAdapter(adapter);
            bool rejected = false;
            try { service.RequestReward("reward", "offer", _ => { }); }
            catch (NotSupportedException) { rejected = true; }
            Require(rejected && adapter.Shows == 0, "Never silently drop a context token on a legacy adapter.");
            AdOutcome? outcome = null;
            service.RequestReward("reward", null, result => outcome = result);
            Require(adapter.Shows == 1 && outcome == AdOutcome.Rewarded, "Null context preserves the legacy route.");
        }

        private sealed class LegacyAdapter : IAdNetworkAdapter
        {
            public int Shows;
            public void Initialize(Action ready) => ready?.Invoke();
            public bool IsInterstitialReady(string placement) => true;
            public bool IsRewardedReady(string placement) => true;
            public void ShowInterstitial(string placement, Action closed) => closed?.Invoke();
            public void ShowRewarded(string placement, Action<bool> completed) { Shows++; completed(true); }
            public void ShowBanner(string placement, string position) { }
            public void HideBanner() { }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task A02_LegacyAwaitReturnsTheOutcomeAfterTheBridgeAcknowledgesItsCallback()
        {
            using var rig = new Rig();
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward");
            Require(!pending.IsCompleted, "The outcome must wait for the ad's result.");

            Require(rig.Provider.CompleteShow(AdResult.Rewarded), "The simulator must finish the requested show.");
            Require(!rig.Controller.PendingReward.HasValue,
                "The legacy callback-return acknowledgement precedes the caller's awaited grant.");
            Require(await pending == AdOutcome.Rewarded, "The legacy await API must still return Rewarded.");
            Require(!rig.Controller.DeliveringReward.HasValue,
                "The callback-scoped receipt must not be presented as an awaited receipt.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void A02_LegacyCancellationStopsOnlyTheWaitAndDoesNotIntroduceReceiptOwnership()
        {
            using var rig = new Rig();
            using var cancellation = new CancellationTokenSource();
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward", cancellation.Token);
            cancellation.Cancel();
            Require(pending.IsCanceled && rig.Provider.IsShowing,
                "Cancelling the legacy wait must leave the already-started ad playing.");
#if UNITY_EDITOR
            LogAssert.Expect(LogType.Warning,
                "[Nexus] Rewarded ad 'reward' granted its reward after the wait was cancelled; the reward was not handed to the caller.");
#endif
            Require(rig.Provider.CompleteShow(AdResult.Rewarded), "The ad must still be able to finish after cancellation.");

            Require(pending.IsCanceled && !rig.Controller.PendingReward.HasValue,
                "Legacy cancellation must not silently change callback-return acknowledgement or return a late reward.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task A02_GrantOverloadRunsTheGrantBeforeAcknowledgementAndBeforeCompletion()
        {
            using var rig = new Rig();
            bool pendingDuringGrant = false, granted = false;
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward", () =>
            {
                pendingDuringGrant = rig.Controller.PendingReward.HasValue;
                granted = true;
            });
            Require(rig.Provider.CompleteShow(AdResult.Rewarded), "The simulator must finish the requested show.");
            Require(granted && pendingDuringGrant, "The grant must run while the receipt is still unacknowledged.");
            Require(!rig.Controller.PendingReward.HasValue, "A returned grant must be acknowledged once.");
            Require(await pending == AdOutcome.Rewarded, "The grant overload must report Rewarded after the grant.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task A02_GrantOverloadFailureKeepsTheReceiptAndFaultsTheTask()
        {
            using var rig = new Rig();
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward", () => throw new System.IO.IOException("save failed"));
            bool thrown = false;
            try { rig.Provider.CompleteShow(AdResult.Rewarded); }
            catch (System.IO.IOException) { thrown = true; }
            Require(thrown, "The failed grant must stay observable at the callback boundary.");
            Require(rig.Controller.PendingReward.HasValue, "A failed grant must leave the reward pending for replay.");
            bool faulted = false;
            try { await pending; }
            catch (System.IO.IOException) { faulted = true; }
            Require(faulted, "The awaited task must report the failed grant, not Rewarded.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task A02_GrantOverloadCancelledWaitStillGrantsALateReward()
        {
            using var rig = new Rig();
            using var cancellation = new CancellationTokenSource();
            int grants = 0;
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward", () => grants++, cancellation.Token);
            cancellation.Cancel();
            Require(rig.Provider.CompleteShow(AdResult.Rewarded), "The ad must still finish after cancellation.");
            Require(grants == 1 && !rig.Controller.PendingReward.HasValue, "A late reward must be granted and acknowledged once.");
            bool cancelled = false;
            try { await pending; }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Only the wait is cancelled.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task A02_GrantOverloadDoesNotGrantNonRewardOutcomes()
        {
            using var rig = new Rig();
            int grants = 0;
            Task<AdOutcome> pending = rig.Service.RequestRewardAsync("reward", () => grants++);
            Require(rig.Provider.CompleteShow(AdResult.Closed), "The simulator must finish the requested show.");
            Require(await pending == AdOutcome.Closed && grants == 0, "A closed ad must not grant.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Rig : IDisposable
        {
            public readonly AdService Service;
            public readonly AdController Controller;
            public readonly SimulatorProvider Provider;

            public Rig()
            {
                var scheduler = new Scheduler();
                Provider = new SimulatorProvider(scheduler) { Interactive = true };
                var config = new AdsConfig(new[] { new AdPlacement("reward", AdFormat.Rewarded) },
                    AdPolicy.Default, new ProviderConfig("", null, false, null, false, false, false, "G"), BuildKind.Editor);
                Controller = new AdController(config, Provider, new Clock(), new Visibility(), new MainThread(),
                    scheduler, new Store(), new Telemetry(), Provider);
                Service = new AdService();
                Service.SetNetworkAdapter(new NexusAdsAdapter(Controller));
            }

            public void Dispose()
            {
                Service.Dispose();
                Controller.Dispose();
                Provider.Cancel();
            }
        }

        private sealed class Clock : IAdClock
        {
            public double Seconds => 1000;
            public long UtcDay => 100;
            public double UtcSeconds => 8641000;
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

        private sealed class Scheduler : IAdScheduler
        {
            private sealed class Handle : IDisposable { public void Dispose() { } }
            public IDisposable Schedule(double seconds, Action action) => new Handle();
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
