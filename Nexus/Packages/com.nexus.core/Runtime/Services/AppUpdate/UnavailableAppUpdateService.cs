using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The update service of a build without a store provider (#273): a check answers
    /// <see cref="AppUpdateAvailability.StoreUnavailable"/>, a start <see cref="AppUpdateOutcome.Unavailable"/>, and the first
    /// call says so in every build's log.
    /// </summary>
    [Preserve]
    public sealed class UnavailableAppUpdateService : NexusService<IAppUpdateService>, IAppUpdateService
    {
        private int _warned;

        public ValueTask<AppUpdateCheck> CheckAsync(CancellationToken ct)
        {
            WarnOnce();
            return new ValueTask<AppUpdateCheck>(new AppUpdateCheck(AppUpdateAvailability.StoreUnavailable));
        }

        public ValueTask<AppUpdateOutcome> StartUpdateAsync(AppUpdateFlow flow, CancellationToken ct)
        {
            WarnOnce();
            return new ValueTask<AppUpdateOutcome>(AppUpdateOutcome.Unavailable);
        }

        public ValueTask<bool> CompleteUpdateAsync(CancellationToken ct)
        {
            WarnOnce();
            return new ValueTask<bool>(false);
        }

        private void WarnOnce()
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                NexusLog.WarnAlways("[AppUpdateService] No store update provider is bound, so this build finds no update "
                    + "(on Android: com.nexus.googleplay's GooglePlayBindings with com.google.play.appupdate).");
        }
    }
}
