using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The review service of a build without a store provider (#272): every request is <see cref="ReviewOutcome.Unavailable"/>,
    /// and the first one says so in every build's log.
    /// </summary>
    [Preserve]
    public sealed class UnavailableReviewService : NexusService<IReviewService>, IReviewService
    {
        private int _warned;

        public ValueTask<ReviewOutcome> RequestReviewAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                NexusLog.WarnAlways("[ReviewService] No store review provider is bound, so no rating sheet appears in this build "
                    + "(on Android: com.nexus.googleplay's GooglePlayBindings with com.google.play.review).");
            return new ValueTask<ReviewOutcome>(ReviewOutcome.Unavailable);
        }
    }
}
