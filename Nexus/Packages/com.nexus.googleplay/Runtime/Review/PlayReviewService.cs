#if NEXUS_PLAY_REVIEW
using System.Threading;
using System.Threading.Tasks;
using Google.Play.Review;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.GooglePlay.Review
{
    /// <summary>
    /// Google Play's in-app review behind <see cref="IReviewService"/> (#272): prepares the flow, then launches it, and completes
    /// when the sheet closes or Play decides not to show it. Google answers the same either way, so the outcome is
    /// <see cref="ReviewOutcome.Completed"/> for both. A failure is written to every build's log with Google's error code.
    /// </summary>
    [Preserve]
    public sealed class PlayReviewService : NexusService<IReviewService>, IReviewService
    {
        private ReviewManager _manager;

        public async ValueTask<ReviewOutcome> RequestReviewAsync(CancellationToken ct)
        {
            _manager ??= new ReviewManager();
            var prepared = await PlayOperations.Wait(_manager.RequestReviewFlow(), ct);
            if (!prepared.IsSuccessful) return Report("prepare", prepared.Error);
            var launched = await PlayOperations.Wait(_manager.LaunchReviewFlow(prepared.GetResult()), ct);
            return launched.IsSuccessful ? ReviewOutcome.Completed : Report("launch", launched.Error);
        }

        internal static ReviewOutcome ToOutcome(ReviewErrorCode error) => error switch
        {
            ReviewErrorCode.NoError => ReviewOutcome.Completed,
            ReviewErrorCode.PlayStoreNotFound => ReviewOutcome.Unavailable,
            _ => ReviewOutcome.Failed,
        };

        private static ReviewOutcome Report(string step, ReviewErrorCode error)
        {
            NexusLog.WarnAlways($"[GooglePlay] The in-app review could not {step} its flow: {error}.");
            return ToOutcome(error);
        }
    }
}
#endif
