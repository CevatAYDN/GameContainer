using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.GooglePlay.Mocks
{
    /// <summary>
    /// The editor's review service (#272): no sheet, a log line per request, and the <see cref="Outcome"/> a test or the game's
    /// editor flow sets (<see cref="ReviewOutcome.Completed"/> by default). <see cref="Requests"/> counts the requests.
    /// </summary>
    [Preserve]
    public sealed class MockReviewService : NexusService<IReviewService>, IReviewService
    {
        public ReviewOutcome Outcome { get; set; } = ReviewOutcome.Completed;
        public int Requests { get; private set; }

        public ValueTask<ReviewOutcome> RequestReviewAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            NexusRuntime.Logger?.Log($"[GooglePlay] Review requested: editor mock, no rating sheet ({Outcome}).");
            return new ValueTask<ReviewOutcome>(Outcome);
        }
    }
}
