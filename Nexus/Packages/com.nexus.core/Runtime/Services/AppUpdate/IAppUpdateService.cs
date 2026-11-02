using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>What the store says about a newer version of the app (#273).</summary>
    public enum AppUpdateAvailability
    {
        /// <summary>The store answered without saying, or its check failed with an error (logged in every build).</summary>
        Unknown,
        /// <summary>No store update in this build: no provider is bound, the store app or its API is missing, or the app was
        /// not installed from the store (a sideloaded build).</summary>
        StoreUnavailable,
        /// <summary>The installed version is the newest the store offers this device.</summary>
        NoUpdate,
        /// <summary>A newer version is on the store; <see cref="AppUpdateCheck.ImmediateAllowed"/> and
        /// <see cref="AppUpdateCheck.FlexibleAllowed"/> say which flow may install it.</summary>
        UpdateAvailable,
        /// <summary>An immediate update the game started earlier is still running; <see cref="AppUpdateFlow.Immediate"/> resumes it.</summary>
        UpdateInProgress,
    }

    /// <summary>How the store installs an update (#273).</summary>
    public enum AppUpdateFlow
    {
        /// <summary>The download runs in the background while the game goes on; <see cref="IAppUpdateService.CompleteUpdateAsync"/>
        /// installs it and restarts the app.</summary>
        Flexible,
        /// <summary>A full-screen store flow downloads and installs the update and restarts the app.</summary>
        Immediate,
    }

    /// <summary>What a started update ended with (#273).</summary>
    public enum AppUpdateOutcome
    {
        /// <summary>The update is downloaded: a flexible one waits for <see cref="IAppUpdateService.CompleteUpdateAsync"/>.</summary>
        Downloaded,
        /// <summary>The player declined the update.</summary>
        Cancelled,
        /// <summary>Nothing to start: no update, the flow is not allowed for it, or no store update in this build.</summary>
        Unavailable,
        /// <summary>The store reported an error (logged in every build).</summary>
        Failed,
    }

    /// <summary>The store's answer to <see cref="IAppUpdateService.CheckAsync"/> (#273).</summary>
    public readonly struct AppUpdateCheck
    {
        public AppUpdateCheck(AppUpdateAvailability availability, int availableVersionCode = 0, int? stalenessDays = null,
            int priority = 0, bool immediateAllowed = false, bool flexibleAllowed = false, bool downloaded = false)
        {
            Availability = availability;
            AvailableVersionCode = availableVersionCode;
            StalenessDays = stalenessDays;
            Priority = priority;
            ImmediateAllowed = immediateAllowed;
            FlexibleAllowed = flexibleAllowed;
            Downloaded = downloaded;
        }

        public AppUpdateAvailability Availability { get; }
        /// <summary>The version code of the store's version, 0 when there is none.</summary>
        public int AvailableVersionCode { get; }
        /// <summary>Days since the store learned of the update; null when it has not (yet) counted them.</summary>
        public int? StalenessDays { get; }
        /// <summary>The priority the release was given on the store (Google Play: 0 to 5, set through its publishing API).</summary>
        public int Priority { get; }
        public bool ImmediateAllowed { get; }
        public bool FlexibleAllowed { get; }
        /// <summary>A flexible update finished downloading earlier; <see cref="IAppUpdateService.CompleteUpdateAsync"/> installs it.</summary>
        public bool Downloaded { get; }
    }

    /// <summary>A newer version of the app on the store, and the store's flow that installs it (#273). Android:
    /// <c>com.nexus.googleplay</c>'s <c>GooglePlayBindings</c>.</summary>
    public interface IAppUpdateService
    {
        /// <summary>Asks the store whether a newer version is available and which flow may install it.</summary>
        ValueTask<AppUpdateCheck> CheckAsync(CancellationToken ct);

        /// <summary>
        /// Starts the store's <paramref name="flow"/> and completes when the update is downloaded, the player declines it, or the
        /// store reports an error. An immediate update restarts the app once installed, so its call may never complete. A
        /// cancelled <paramref name="ct"/> stops the wait, not the store's flow.
        /// </summary>
        ValueTask<AppUpdateOutcome> StartUpdateAsync(AppUpdateFlow flow, CancellationToken ct);

        /// <summary>
        /// Installs a downloaded flexible update. The store restarts the app, so the call returns only when it could not
        /// install: false, logged in every build (also when no update was downloaded).
        /// </summary>
        ValueTask<bool> CompleteUpdateAsync(CancellationToken ct);
    }
}
