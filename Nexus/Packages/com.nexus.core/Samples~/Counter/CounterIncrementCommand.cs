using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Sequential (default) command: updates the model and records telemetry.
    /// Demonstrates the standard 0-GC, AOT-friendly generic command pattern,
    /// plus injection and use of a built-in Nexus service (IFeedbackService).
    /// </summary>
    public class CounterIncrementCommand : ICommand<CounterSignal>
    {
        [Inject] public ICounterModel Model { get; set; }
        [Inject] public ICounterTelemetryService Telemetry { get; set; }
        [Inject] public IFeedbackService Feedback { get; set; }

        public void Execute(CounterSignal signal)
        {
            Model.Increment(signal.Amount);
            Telemetry.RecordIncrement(Model.Count.Value);
            Feedback?.Play(FeedbackPreset.LightClick);
        }
    }
}
