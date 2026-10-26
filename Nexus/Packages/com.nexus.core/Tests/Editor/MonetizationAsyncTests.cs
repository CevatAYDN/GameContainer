using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #73: RequestReward and Purchase were callback-only, so an IAsyncCommand had nothing to await and a pooled
    /// command's injected members were cleared before the answer arrived (Wickling's ClaimRewardCommand captured locals).
    /// </summary>
    public sealed class MonetizationAsyncTests
    {
        private sealed class LaterAdAdapter : IAdNetworkAdapter, IAdOutcomeAdapter
        {
            public Action<AdOutcome> Answer;
            public void Initialize(Action onInitialized) => onInitialized?.Invoke();
            public bool IsInterstitialReady(string placement) => true;
            public void ShowInterstitial(string placement, Action onClosed) => onClosed?.Invoke();
            public bool IsRewardedReady(string placement) => true;
            public void ShowRewarded(string placement, Action<bool> onCompleted) => throw new NotSupportedException();
            public void RequestReward(string placement, Action<AdOutcome> onOutcome) => Answer = onOutcome;
            public void ShowBanner(string placement, string position) { }
            public void HideBanner() { }
        }

        private sealed class LaterStoreAdapter : IIapStoreAdapter, IIapOutcomeAdapter
        {
            public Action<PurchaseOutcome> Answer;
            public void Initialize(List<ProductDefinition> products, Action<bool> onInitialized) => onInitialized?.Invoke(true);
            public void Purchase(string productId, Action<bool, string> onComplete) => throw new NotSupportedException();
            public void PurchaseWithOutcome(string productId, Action<PurchaseOutcome> onOutcome) => Answer = onOutcome;
            public void Restore(Action<bool> onComplete) => onComplete?.Invoke(true);
            public bool IsOwned(string productId) => false;
        }

        public struct ClaimReward { }

        public sealed class Wallet { public int Coins; }

        [SignalHandler(typeof(ClaimReward))]
        public sealed class ClaimRewardCommand : IAsyncCommand<ClaimReward>
        {
            [Inject] public IAdService Ads;
            [Inject] public Wallet Wallet;

            public async ValueTask ExecuteAsync(ClaimReward signal, CancellationToken ct)
            {
                if (await Ads.RequestRewardAsync("level_end", ct) == AdOutcome.Rewarded)
                    Wallet.Coins += 50;
            }
        }

        private static async Task<(AdService, LaterAdAdapter)> Ads()
        {
            var ads = new AdService();
            await ads.InitializeAsync(default);
            var adapter = new LaterAdAdapter();
            ads.SetNetworkAdapter(adapter);
            return (ads, adapter);
        }

        [Test]
        public async Task AnAsyncCommandGrantsAfterAnAwaitedRewardThatArrivesLater()
        {
            var (ads, adapter) = await Ads();
            using (ads)
            using (var test = NexusTestHarness.CreateContext())
            {
                test.BindInstance<IAdService>(ads);
                test.Register<Wallet>();
                test.RegisterAsyncCommand<ClaimRewardCommand>();

                var claim = test.DispatchAsync(new ClaimReward()).AsTask();
                Assert.That(claim.IsCompleted, Is.False, "the command waits for the ad");
                adapter.Answer(AdOutcome.Rewarded);
                await claim;

                Assert.That(test.GetModel<Wallet>().Coins, Is.EqualTo(50));
            }
        }

        [Test]
        public async Task CancellingTheRewardWaitEndsItAndALateRewardIsLogged()
        {
            var (ads, adapter) = await Ads();
            using (ads)
            using (var cts = new CancellationTokenSource())
            {
                var wait = ads.RequestRewardAsync("level_end", cts.Token);
                cts.Cancel();
                Assert.That(wait.IsCanceled, Is.True);

                LogAssert.Expect(LogType.Warning, new Regex("granted its reward after the wait was cancelled"));
                adapter.Answer(AdOutcome.Rewarded);
            }
        }

        [Test]
        public async Task APurchaseAwaitReturnsTheOutcomeTheStoreReportsLater()
        {
            using var iap = new IapService();
            await iap.InitializeAsync(default);
            var store = new LaterStoreAdapter();
            iap.SetStoreAdapter(store);

            var purchase = iap.PurchaseAsync("pass");
            Assert.That(purchase.IsCompleted, Is.False);
            store.Answer(PurchaseOutcome.Pending);

            Assert.That(await purchase, Is.EqualTo(PurchaseOutcome.Pending));
        }
    }
}
