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
        /// <remarks>
        /// This is an outcome-only wait, not a persistence or receipt-acknowledgement boundary. An adapter that
        /// acknowledges on callback return can acknowledge before the caller's awaited grant is saved. Games that
        /// require confirmed reward persistence must grant and confirm inside the synchronous
        /// <see cref="IAdService.RequestReward"/> callback, following their adapter's receipt contract.
        /// </remarks>
        public static Task<AdOutcome> RequestRewardAsync(this IAdService ads, string placement, CancellationToken ct = default)
        {
            if (ads == null) throw new ArgumentNullException(nameof(ads));
            return Await<AdOutcome>(ct, done => ads.RequestReward(placement, done), outcome => outcome == AdOutcome.Rewarded,
                $"Rewarded ad '{placement}' granted its reward after the wait was cancelled; the reward was not handed to the caller.");
        }

        /// <summary>
        /// Shows a rewarded ad and runs <paramref name="grant"/> inside the outcome callback, before the adapter's
        /// callback-return acknowledgement; the task completes with <see cref="AdOutcome.Rewarded"/> only after the grant
        /// returned (#382). A throwing grant faults the task and, with a receipt-keeping adapter, leaves the reward pending.
        /// Cancelling stops the wait only: a reward earned afterwards is still granted, never dropped.
        /// </summary>
        /// <param name="grant">Synchronous grant that must persist (and throw on a failed save) before it returns.</param>
        public static Task<AdOutcome> RequestRewardAsync(this IAdService ads, string placement, Action grant, CancellationToken ct = default)
            => RequestRewardAsync(ads, placement, null, grant, ct);

        /// <summary>Contextual grant-before-ack path. The synchronous grant reads its adapter's delivering receipt;
        /// context is the offer key and the receipt id remains the idempotency key.</summary>
        public static Task<AdOutcome> RequestRewardAsync(this IAdService ads, string placement, string context, Action grant, CancellationToken ct = default)
        {
            if (ads == null) throw new ArgumentNullException(nameof(ads));
            if (grant == null) throw new ArgumentNullException(nameof(grant));
            if (ct.IsCancellationRequested) return Task.FromCanceled<AdOutcome>(ct);
            var tcs = new TaskCompletionSource<AdOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            try
            {
                ads.RequestReward(placement, context, outcome =>
                {
                    registration.Dispose();
                    if (outcome == AdOutcome.Rewarded)
                    {
                        try { grant(); }
                        catch (Exception e)
                        {
                            tcs.TrySetException(e);
                            throw;
                        }
                    }
                    tcs.TrySetResult(outcome);
                });
            }
            catch
            {
                registration.Dispose();
                throw;
            }
            return tcs.Task;
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
                        NexusLog.WarnAlways("[Nexus] " + lateGrantWarning);
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
