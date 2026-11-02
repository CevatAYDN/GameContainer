using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine.Scripting;

namespace Nexus.GooglePlay.Mocks
{
    /// <summary>
    /// The editor's update service (#273): no store flow, a log line per call, and the <see cref="Check"/> and
    /// <see cref="Outcome"/> a test or the game's editor flow sets (no update and <see cref="AppUpdateOutcome.Downloaded"/> by
    /// default). <see cref="Starts"/> and <see cref="Completions"/> count the calls.
    /// </summary>
    [Preserve]
    public sealed class MockAppUpdateService : NexusService<IAppUpdateService>, IAppUpdateService
    {
        public AppUpdateCheck Check { get; set; } = new AppUpdateCheck(AppUpdateAvailability.NoUpdate);
        public AppUpdateOutcome Outcome { get; set; } = AppUpdateOutcome.Downloaded;
        public int Starts { get; private set; }
        public int Completions { get; private set; }

        public ValueTask<AppUpdateCheck> CheckAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<AppUpdateCheck>(Check);
        }

        public ValueTask<AppUpdateOutcome> StartUpdateAsync(AppUpdateFlow flow, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Starts++;
            NexusRuntime.Logger?.Log($"[GooglePlay] {flow} update started: editor mock, no store flow ({Outcome}).");
            return new ValueTask<AppUpdateOutcome>(Outcome);
        }

        public ValueTask<bool> CompleteUpdateAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Completions++;
            NexusRuntime.Logger?.Log("[GooglePlay] Update completion requested: editor mock, the app does not restart.");
            return new ValueTask<bool>(true);
        }
    }
}
