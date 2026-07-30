using Nexus.Core;

namespace Nexus.Starter
{
    /// <summary>
    /// Handles NexusStartSignal: increments the model's counter.
    /// Commands are auto-pooled by the CommandPoolManager for 0-GC steady state.
    /// </summary>
    public class NexusStartCommand : ICommand<NexusStartSignal>
    {
        [Inject] private INexusStartModel _model;

        public void Execute(NexusStartSignal signal)
        {
            _model.Increment(signal.Value);
        }
    }
}
