#if NEXUS_PLAY_REVIEW
using Google.Play.Review;
using NUnit.Framework;
using Nexus.Core.Services;
using Nexus.GooglePlay.Review;

namespace Nexus.GooglePlay.Tests
{
    /// <summary>#272: the Play review service over Google's plugin, whose operations complete at once in the editor.</summary>
    public sealed class PlayReviewServiceTests
    {
        // An operation already done when the service subscribes must still end the wait; one that missed it would never complete.
        [Test]
        public void RequestReviewAsync_CompletesOverGooglesEditorFlow()
        {
            var pending = new PlayReviewService().RequestReviewAsync(default);
            Assert.IsTrue(pending.IsCompleted, "the wait missed an operation Google had already completed");
            Assert.AreEqual(ReviewOutcome.Completed, pending.Result);
        }

        [Test]
        public void GooglesErrors_MapToUnavailableWithoutAStore_AndFailedOtherwise()
        {
            Assert.AreEqual(ReviewOutcome.Unavailable, PlayReviewService.ToOutcome(ReviewErrorCode.PlayStoreNotFound));
            Assert.AreEqual(ReviewOutcome.Failed, PlayReviewService.ToOutcome(ReviewErrorCode.ErrorRequestingFlow));
            Assert.AreEqual(ReviewOutcome.Failed, PlayReviewService.ToOutcome(ReviewErrorCode.ErrorLaunchingFlow));
        }
    }
}
#endif
