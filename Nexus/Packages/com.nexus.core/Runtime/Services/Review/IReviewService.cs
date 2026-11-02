using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>
    /// What a review request ended with (#272). The store does not say whether its sheet appeared or what the player
    /// rated, and Google's policy forbids acting on it, so <see cref="Completed"/> says only that the flow ran to its end.
    /// </summary>
    public enum ReviewOutcome
    {
        /// <summary>The store's flow ran to its end; the store decides whether the sheet appears (it limits how often).</summary>
        Completed,
        /// <summary>No store review in this build: no provider is bound, or the store app is missing.</summary>
        Unavailable,
        /// <summary>The store reported an error while preparing or showing its flow.</summary>
        Failed,
    }

    /// <summary>The store's in-app rating sheet (#272). Android: <c>com.nexus.googleplay</c>'s <c>GooglePlayBindings</c>.</summary>
    public interface IReviewService
    {
        /// <summary>
        /// Asks the store for its rating sheet and completes when its flow ends. Ask at a natural pause, not from a button
        /// that promises a rating screen, and never reward or gate anything on it (Google's policy).
        /// </summary>
        ValueTask<ReviewOutcome> RequestReviewAsync(CancellationToken ct);
    }
}
