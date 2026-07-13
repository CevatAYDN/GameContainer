using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Bound with ExecutionMode.Concurrent (see CounterLifecycle).
    /// Concurrent handlers for the same signal run in parallel — ideal for
    /// independent I/O-bound work. Here it just logs; a real handler would
    /// fetch remote config, load assets, etc.
    /// </summary>
    public class CounterLoadCommand : ICommand<CounterLoadSignal>
    {
        [Inject] public ICounterTelemetryService Telemetry { get; set; }

        public void Execute(CounterLoadSignal signal)
        {
            Debug.Log("[Counter] Concurrent load started");
        }
    }
}
