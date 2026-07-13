using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Async command (IAsyncCommand). Demonstrates the await-based path and the
    /// [CommandTimeout] attribute: if execution exceeds the timeout, the bus
    /// cancels via the CancellationToken.
    /// </summary>
    [CommandTimeout(2000)]
    public class CounterAsyncCommand : IAsyncCommand<CounterAsyncSignal>
    {
        [Inject] public ICounterModel Model { get; set; }
        [Inject] public ICounterTelemetryService Telemetry { get; set; }

        public async ValueTask ExecuteAsync(CounterAsyncSignal signal, CancellationToken ct)
        {
            // Simulate async I/O (e.g. fetching from a remote config).
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            Model.Increment(signal.Payload);
            Telemetry.RecordIncrement(Model.Count.Value);
            Debug.Log($"[Counter] Async load applied +{signal.Payload}");
        }
    }
}
