using Nexus.Core;

namespace Nexus.Samples.Counter
{
    public class CounterIncrementCommand : ICommand<CounterSignal>
    {
        [Inject] public ICounterModel Model { get; set; }

        public void Execute(CounterSignal signal)
        {
            Model.Increment(signal.Amount);
        }
    }
}
