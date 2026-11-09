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
            void OnCompleted(AppUpdateRequest finished) => done.TrySetResult(finished);
            request.Completed += OnCompleted;
            try
            {
                using (ct.Register(() => done.TrySetCanceled(ct)))
                {
                    while (!done.Task.IsCompleted)
                    {
                        ct.ThrowIfCancellationRequested();
                        // SDK 1.8.3 can publish terminal status without raising Completed (#389). Its manager then keeps
                        // the request as in progress and refuses every later StartUpdate, so the request is ended (#391).
                        if (request.Status == AppUpdateStatus.Failed || request.Status == AppUpdateStatus.Canceled)
                        {
                            EndStatusOnlyRequest(request);
                            return request;
                        }
                        await Task.WhenAny(done.Task, Task.Delay(100, ct));
                    }
                    return await done.Task;
                }
            }
            finally { request.Completed -= OnCompleted; }
        }

        // Ends the request through the SDK's own error path, which raises Completed and frees the manager's single slot.
        private static void EndStatusOnlyRequest(AppUpdateRequest request)
        {
            if (request.IsDone) return;
            var error = request.Status == AppUpdateStatus.Canceled
                ? AppUpdateErrorCode.ErrorUserCanceled
                : AppUpdateErrorCode.ErrorUpdateFailed;
            var end = request.GetType().GetMethod("OnErrorOccurred", new[] { typeof(AppUpdateErrorCode) });
            if (end == null)
            {
                Nexus.Core.Services.NexusLog.WarnAlways("[GooglePlay] The in-app update SDK changed: a status-only result could not end its request, so a later update start may be refused.");
                return;
            }
            end.Invoke(request, new object[] { error });
        }
#endif
    }
}
#endif
