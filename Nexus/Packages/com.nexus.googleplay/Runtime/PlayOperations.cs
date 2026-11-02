#if NEXUS_PLAY_REVIEW || NEXUS_PLAY_APPUPDATE
using System.Threading;
using System.Threading.Tasks;
using Google.Play.Common;
#if NEXUS_PLAY_APPUPDATE
using Google.Play.AppUpdate;
#endif

namespace Nexus.GooglePlay
{
    /// <summary>Awaits Google's operations; a cancelled token stops the wait, not Google's flow.</summary>
    internal static class PlayOperations
    {
        public static async Task<PlayAsyncOperation<TResult, TError>> Wait<TResult, TError>(PlayAsyncOperation<TResult, TError> operation, CancellationToken ct)
        {
            var done = new TaskCompletionSource<PlayAsyncOperation<TResult, TError>>();
            operation.Completed += finished => done.TrySetResult(finished); // called at once for an operation already done
            using (ct.Register(() => done.TrySetCanceled(ct)))
                return await done.Task;
        }
#if NEXUS_PLAY_APPUPDATE

        public static async Task<AppUpdateRequest> Wait(AppUpdateRequest request, CancellationToken ct)
        {
            var done = new TaskCompletionSource<AppUpdateRequest>();
            request.Completed += finished => done.TrySetResult(finished); // called at once for a request already done
            using (ct.Register(() => done.TrySetCanceled(ct)))
                return await done.Task;
        }
#endif
    }
}
#endif
