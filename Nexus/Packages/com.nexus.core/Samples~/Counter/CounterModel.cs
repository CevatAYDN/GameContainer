using Nexus.Core;

namespace Nexus.Samples.Counter
{
    public class CounterModel : ICounterModel, IReactiveModel
    {
        public ObservableProperty<int> Count { get; } = new(0);
        int ICounterModel.Count => Count.Value;

        public void Increment(int amount)
        {
            if (amount <= 0) return;
            Count.Value += amount;
        }

        public void OnBind(IContext context)
        {
        }
    }
}
