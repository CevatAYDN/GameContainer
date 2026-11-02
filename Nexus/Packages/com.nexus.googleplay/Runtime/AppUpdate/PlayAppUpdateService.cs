#if NEXUS_PLAY_APPUPDATE
using System.Threading;
using System.Threading.Tasks;
using Google.Play.AppUpdate;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.GooglePlay.AppUpdate
{
    /// <summary>
    /// Google Play's in-app update behind <see cref="IAppUpdateService"/> (#273). Google's <see cref="AppUpdateManager"/> reaches
    /// Java and adds a handler to the scene when it is made, so it is made at the first call, not when the service is bound.
    /// An error is written to every build's log with Google's error code; a declined update is not an error.
    /// </summary>
    [Preserve]
    public sealed class PlayAppUpdateService : NexusService<IAppUpdateService>, IAppUpdateService
    {
        private AppUpdateManager _manager;

        private AppUpdateManager Manager => _manager ??= new AppUpdateManager();

        public async ValueTask<AppUpdateCheck> CheckAsync(CancellationToken ct)
        {
            var read = await PlayOperations.Wait(Manager.GetAppUpdateInfo(), ct);
            if (!read.IsSuccessful)
            {
                Warn("check for an update", read.Error);
                return new AppUpdateCheck(AvailabilityAfter(read.Error));
            }
            var info = read.GetResult();
            return new AppUpdateCheck(ToAvailability(info.UpdateAvailability), info.AvailableVersionCode,
                info.ClientVersionStalenessDays, info.UpdatePriority,
                immediateAllowed: info.IsUpdateTypeAllowed(AppUpdateOptions.ImmediateAppUpdateOptions()),
                flexibleAllowed: info.IsUpdateTypeAllowed(AppUpdateOptions.FlexibleAppUpdateOptions()),
                downloaded: info.AppUpdateStatus == AppUpdateStatus.Downloaded);
        }

        public async ValueTask<AppUpdateOutcome> StartUpdateAsync(AppUpdateFlow flow, CancellationToken ct)
        {
            // Google's AppUpdateInfo starts one flow only, so each start reads a fresh one.
            var read = await PlayOperations.Wait(Manager.GetAppUpdateInfo(), ct);
            if (!read.IsSuccessful) return Report("check for an update", read.Error);
            var info = read.GetResult();
            var options = flow == AppUpdateFlow.Immediate
                ? AppUpdateOptions.ImmediateAppUpdateOptions()
                : AppUpdateOptions.FlexibleAppUpdateOptions();
            // Google's StartUpdate refuses these two itself, but then never clears its request, and every later start on the
            // manager throws "Another update flow is already in progress" (com.google.play.appupdate 1.8.3).
            if (info.UpdateAvailability == UpdateAvailability.UpdateNotAvailable || !info.IsUpdateTypeAllowed(options))
                return AppUpdateOutcome.Unavailable;
            var request = await PlayOperations.Wait(Manager.StartUpdate(info, options), ct);
            return request.Error == AppUpdateErrorCode.NoError ? AppUpdateOutcome.Downloaded : Report("run its flow", request.Error);
        }

        public async ValueTask<bool> CompleteUpdateAsync(CancellationToken ct)
        {
            // On success Play installs the update and restarts the app, so the wait ends only on a failure.
            var done = await PlayOperations.Wait(Manager.CompleteUpdate(), ct);
            if (done.IsSuccessful) return true;
            Warn("install the downloaded update", done.Error);
            return false;
        }

        internal static AppUpdateAvailability ToAvailability(UpdateAvailability availability) => availability switch
        {
            UpdateAvailability.UpdateNotAvailable => AppUpdateAvailability.NoUpdate,
            UpdateAvailability.UpdateAvailable => AppUpdateAvailability.UpdateAvailable,
            UpdateAvailability.DeveloperTriggeredUpdateInProgress => AppUpdateAvailability.UpdateInProgress,
            _ => AppUpdateAvailability.Unknown,
        };

        internal static AppUpdateAvailability AvailabilityAfter(AppUpdateErrorCode error) =>
            WithoutStore(error) ? AppUpdateAvailability.StoreUnavailable : AppUpdateAvailability.Unknown;

        internal static AppUpdateOutcome ToOutcome(AppUpdateErrorCode error) => error switch
        {
            AppUpdateErrorCode.NoError => AppUpdateOutcome.Downloaded,
            AppUpdateErrorCode.ErrorUserCanceled => AppUpdateOutcome.Cancelled,
            AppUpdateErrorCode.ErrorUpdateUnavailable or AppUpdateErrorCode.ErrorUpdateNotAllowed => AppUpdateOutcome.Unavailable,
            _ when WithoutStore(error) => AppUpdateOutcome.Unavailable,
            _ => AppUpdateOutcome.Failed,
        };

        // No Play Store, no Play API, or an app not installed from Play (a sideloaded build).
        private static bool WithoutStore(AppUpdateErrorCode error) => error == AppUpdateErrorCode.ErrorApiNotAvailable
            || error == AppUpdateErrorCode.ErrorPlayStoreNotFound || error == AppUpdateErrorCode.ErrorAppNotOwned;

        private static AppUpdateOutcome Report(string step, AppUpdateErrorCode error)
        {
            var outcome = ToOutcome(error);
            if (outcome != AppUpdateOutcome.Cancelled) Warn(step, error);
            return outcome;
        }

        private static void Warn(string step, AppUpdateErrorCode error) =>
            NexusLog.WarnAlways($"[GooglePlay] The in-app update could not {step}: {error}.");
    }
}
#endif
