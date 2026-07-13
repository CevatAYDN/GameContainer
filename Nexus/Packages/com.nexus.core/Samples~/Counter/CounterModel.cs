using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Reactive model: holds the counter value and notifies subscribers on change.
    /// Implements <see cref="IReactiveModel"/> so the runtime calls OnBind() once
    /// after all [Inject] dependencies are resolved.
    /// </summary>
    public class CounterModel : ICounterModel, IReactiveModel
    {
        public ObservableProperty<int> Count { get; } = new(0);

        public void Increment(int amount)
        {
            if (amount <= 0) return;
            Count.Value += amount;
        }

        // Called by the Nexus runtime once, after constructor injection completes.
        public ValueTask OnBind(CancellationToken ct) => default;
    }
}
