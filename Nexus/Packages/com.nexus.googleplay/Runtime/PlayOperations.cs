#if NEXUS_PLAY_REVIEW
using System.Threading;
using System.Threading.Tasks;
using Google.Play.Common;

namespace Nexus.GooglePlay
{
    /// <summary>Awaits Google's <see cref="PlayAsyncOperation{TResult, TError}"/>; a cancelled token stops the wait, not Google's flow.</summary>
    internal static class PlayOperations
    {
        public static async Task<PlayAsyncOperation<TResult, TError>> Wait<TResult, TError>(PlayAsyncOperation<TResult, TError> operation, CancellationToken ct)
        {
            var done = new TaskCompletionSource<PlayAsyncOperation<TResult, TError>>();
            operation.Completed += finished => done.TrySetResult(finished); // called at once for an operation already done
            using (ct.Register(() => done.TrySetCanceled(ct)))
                return await done.Task;
        }
    }
}
#endif
