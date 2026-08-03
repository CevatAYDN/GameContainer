using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Starter
{
    /// <summary>Read-only view of the starter model.</summary>
    public interface INexusStartModel
    {
        ObservableProperty<int> Counter { get; }
        void Increment(int amount);
    }

    /// <summary>
    /// Reactive model: holds observable state.
    /// Changes to ObservableProperty<T> automatically notify subscribers.
    /// </summary>
    public class NexusStartModel : INexusStartModel, IReactiveModel
    {
        public ObservableProperty<int> Counter { get; } = new(0);

        public ValueTask OnBind(CancellationToken ct) => default;

        public void Increment(int amount)
        {
            if (amount > 0)
                Counter.Value += amount;
        }
    }
}
