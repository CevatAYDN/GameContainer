#if NEXUS_PLAY_APPUPDATE
using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Play.AppUpdate;
using Nexus.Core.Services;
using Nexus.GooglePlay.AppUpdate;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Nexus.Editor.Tests
{
    // Real SDK request state transitions, without constructing its Java-facing manager.
    public sealed class FullReviewPlatformPlayUpdateTests
    {
        [TestCase(AppUpdateStatus.Failed, AppUpdateOutcome.Failed)]
        [TestCase(AppUpdateStatus.Canceled, AppUpdateOutcome.Cancelled)]
        public async Task P05_TerminalSdkStatusWithoutCompletedEvent_EndsTheAdapterWait(
            AppUpdateStatus status, AppUpdateOutcome expected)
        {
            var request = NewSdkRequest();
            using var cancellation = new CancellationTokenSource();
            Task<AppUpdateOutcome> result = PlayAppUpdateService.WaitForResultAsync(request, cancellation.Token).AsTask();
            try
            {
                Invoke(request, "UpdateState", status, 0L, 0L);
                Assert.That(request.Status, Is.EqualTo(status));
                Assert.That(request.IsDone, Is.False, "Pinned SDK UpdateState does not finish these statuses.");
                Assert.That(request.Error, Is.EqualTo(AppUpdateErrorCode.NoError));
                Task finished = await Task.WhenAny(result, Task.Delay(1500));
                Assert.That(finished, Is.SameAs(result),
                    "A terminal SDK status must not require caller cancellation to release the service wait.");
                Assert.That(await result, Is.EqualTo(expected));
            }
            finally
            {
                cancellation.Cancel();
                try { await result; }
                catch (OperationCanceledException) { }
            }
        }

        [Test]
        public async Task P05_LateFailedActivityResultWithNoError_IsNotReportedAsDownloaded()
        {
            var request = NewSdkRequest();
            Invoke(request, "OnUpdateDownloaded");
            Invoke(request, "SetUpdateActivityResult", 1); // SDK ActivityResult.ResultFailed.
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Status, Is.EqualTo(AppUpdateStatus.Failed));
            Assert.That(request.Error, Is.EqualTo(AppUpdateErrorCode.NoError),
                "The pinned SDK keeps the earlier success error code after this late callback.");

            var result = await PlayAppUpdateService.WaitForResultAsync(request, CancellationToken.None);

            Assert.That(result, Is.EqualTo(AppUpdateOutcome.Failed));
        }

        [Test]
        public async Task DownloadedSdkRequest_IsReportedAsDownloaded()
        {
            var request = NewSdkRequest();
            Invoke(request, "OnUpdateDownloaded");
            Assert.That(await PlayAppUpdateService.WaitForResultAsync(request, CancellationToken.None),
                Is.EqualTo(AppUpdateOutcome.Downloaded));
        }

        [Test]
        public async Task P05_LateAcceptedActivityResult_PreservesAnAlreadyDownloadedResult()
        {
            var request = NewSdkRequest();
            Invoke(request, "OnUpdateDownloaded");
            Invoke(request, "SetUpdateActivityResult", -1); // SDK ActivityResult.ResultOk.
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.DownloadProgress, Is.EqualTo(1f));
            Assert.That(request.Status, Is.EqualTo(AppUpdateStatus.Pending));
            Assert.That(request.Error, Is.EqualTo(AppUpdateErrorCode.NoError));

            var result = await PlayAppUpdateService.WaitForResultAsync(request, CancellationToken.None);

            Assert.That(result, Is.EqualTo(AppUpdateOutcome.Downloaded),
                "A late accepted-dialog callback must not turn a completed download into a failure.");
        }

        [Test]
        public async Task PendingSdkRequest_CallerCancellationEndsOnlyTheWait()
        {
            var request = NewSdkRequest();
            using var cancellation = new CancellationTokenSource();
            Task<AppUpdateOutcome> result = PlayAppUpdateService.WaitForResultAsync(request, cancellation.Token).AsTask();
            Assert.That(result.IsCompleted, Is.False);
            cancellation.Cancel();
            bool cancelled = false;
            try { await result; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True);
            Assert.That(request.IsDone, Is.False);
        }

        [TestCase(AppUpdateStatus.Failed, AppUpdateErrorCode.ErrorUpdateFailed)]
        [TestCase(AppUpdateStatus.Canceled, AppUpdateErrorCode.ErrorUserCanceled)]
        public async Task P06_StatusOnlyTerminationEndsTheSdkRequestSoTheManagerAcceptsAnotherStart(
            AppUpdateStatus status, AppUpdateErrorCode expectedError)
        {
            var request = NewSdkRequest();
            int completed = 0;
            // The pinned manager frees its single in-progress slot from this event (AppUpdateManagerInternal.StartUpdateInternal).
            request.Completed += _ => completed++;
            Invoke(request, "UpdateState", status, 0L, 0L);
            Assert.That(request.IsDone, Is.False);

            await PlayAppUpdateService.WaitForResultAsync(request, CancellationToken.None);

            Assert.That(request.IsDone, Is.True, "A status-only terminal request must be ended.");
            Assert.That(completed, Is.EqualTo(1), "Completed must fire once so the SDK manager releases the request.");
            Assert.That(request.Error, Is.EqualTo(expectedError));
        }

        [Test]
        public void P06_EveryServiceInstanceSharesOneSdkManager()
        {
            var previous = PlayAppUpdateService.CreateManager;
            int made = 0;
            PlayAppUpdateService.ResetSharedManagerForTests();
            PlayAppUpdateService.CreateManager = () =>
            {
                made++;
                // The real constructor reaches Java and adds a scene handler; identity is all this test needs.
                return (AppUpdateManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AppUpdateManager));
            };
            try
            {
                new PlayAppUpdateService();
                var first = PlayAppUpdateService.Manager;
                for (int i = 0; i < 5; i++)
                {
                    new PlayAppUpdateService();
                    Assert.That(PlayAppUpdateService.Manager, Is.SameAs(first));
                }
                Assert.That(made, Is.EqualTo(1), "Repeated service ownership must not make another SDK manager and handler.");
            }
            finally
            {
                PlayAppUpdateService.CreateManager = previous;
                PlayAppUpdateService.ResetSharedManagerForTests();
            }
        }

        private static AppUpdateRequest NewSdkRequest()
        {
            var assembly = typeof(AppUpdateRequest).Assembly;
            var package = PackageInfo.FindForAssembly(assembly);
            if (package == null || package.version != "1.8.3")
                Assert.Inconclusive("This controlled SDK fixture requires com.google.play.appupdate 1.8.3.");
            var type = assembly.GetType("Google.Play.AppUpdate.Internal.AppUpdateRequestImpl", true);
            return (AppUpdateRequest)Activator.CreateInstance(type, nonPublic: true);
        }

        private static void Invoke(AppUpdateRequest request, string method, params object[] arguments)
        {
            var target = request.GetType().GetMethod(method);
            Assert.That(target, Is.Not.Null, "The pinned SDK request boundary changed.");
            target.Invoke(request, arguments);
        }
    }
}
#endif
