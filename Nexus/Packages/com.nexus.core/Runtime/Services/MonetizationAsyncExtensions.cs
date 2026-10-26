using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Awaitable forms of <see cref="IAdService.RequestReward"/> and <see cref="IIapService.Purchase"/> for an
    /// <see cref="IAsyncCommand"/>: a pooled command's injected members are cleared when it returns, so a callback that
    /// arrives later found them null (issue #73). The continuation runs asynchronously, never inside the SDK's callback.
    /// </summary>
    public static class MonetizationAsyncExtensions
    {
        /// <summary>
        /// Shows a rewarded ad and completes with its outcome. Cancelling stops the wait, not the ad: an ad already on
        /// screen keeps playing and a reward it grants afterwards is not reported here (a warning is logged).
        /// </summary>
        public static Task<AdOutcome> RequestRewardAsync(this IAdService ads, string placement, CancellationToken ct = default)
        {
            if (ads == null) throw new ArgumentNullException(nameof(ads));
            return Await<AdOutcome>(ct, done => ads.RequestReward(placement, done), outcome => outcome == AdOutcome.Rewarded,
                $"Rewarded ad '{placement}' granted its reward after the wait was cancelled; the reward was not handed to the caller.");
        }

        /// <summary>
        /// Starts a purchase and completes with its outcome. Cancelling stops the wait, not the store: a purchase the store
        /// completes afterwards is not reported here (a warning is logged); a non-consumable still shows in
        /// <see cref="IIapService.IsProductOwned"/>, so grant from ownership or pass no token for consumables.
        /// </summary>
        public static Task<PurchaseOutcome> PurchaseAsync(this IIapService iap, string productId, CancellationToken ct = default)
        {
            if (iap == null) throw new ArgumentNullException(nameof(iap));
            return Await<PurchaseOutcome>(ct, done => iap.Purchase(productId, done), outcome => outcome == PurchaseOutcome.Purchased,
                $"Purchase of '{productId}' completed after the wait was cancelled; the content was not handed to the caller.");
        }

        private static Task<T> Await<T>(CancellationToken ct, Action<Action<T>> start, Func<T, bool> isGrant, string lateGrantWarning)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            try
            {
                start(outcome =>
                {
                    registration.Dispose();
                    if (!tcs.TrySetResult(outcome) && isGrant(outcome))
                        NexusRuntime.Logger?.LogWarning("[Nexus] " + lateGrantWarning);
                });
            }
            catch
            {
                registration.Dispose();
                throw;
            }
            return tcs.Task;
        }
    }
}
