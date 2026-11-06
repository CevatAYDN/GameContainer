using Nexus.Core;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Sequential (default) command: updates the model and records telemetry.
    /// Demonstrates the standard 0-GC, AOT-friendly generic command pattern
    /// plus injection of the model and of a custom service.
    /// </summary>
    public class CounterIncrementCommand : ICommand<CounterSignal>
    {
        [Inject] public ICounterModel Model { get; set; }
        [Inject] public ICounterTelemetryService Telemetry { get; set; }

        public void Execute(CounterSignal signal)
        {
            Model.Increment(signal.Amount);
            Telemetry.RecordIncrement(Model.Count.Value);
        }
    }
}
