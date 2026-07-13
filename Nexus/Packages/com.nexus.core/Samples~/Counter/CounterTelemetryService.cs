using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Samples.Counter
{
    public interface ICounterTelemetryService
    {
        void RecordIncrement(int total);
        int TotalIncrements { get; }
    }

    /// <summary>
    /// Custom Nexus service (INexusService). Demonstrates the service lifecycle:
    /// BindService auto-calls InitializeAsync after configuration and OnDispose
    /// on context shutdown. Injected into commands via [Inject].
    /// </summary>
    [Preserve]
    public class CounterTelemetryService : ICounterTelemetryService, INexusService
    {
        public int TotalIncrements { get; private set; }

        public void RecordIncrement(int total)
        {
            TotalIncrements++;
            UnityEngine.Debug.Log($"[Counter] increment #{TotalIncrements} -> total {total}");
        }

        public ValueTask InitializeAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
