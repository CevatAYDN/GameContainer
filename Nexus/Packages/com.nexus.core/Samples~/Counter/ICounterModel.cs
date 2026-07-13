using Nexus.Core;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Reactive counter state. Exposing the <see cref="ObservableProperty{T}"/> directly
    /// lets views subscribe to change notifications (oldValue, newValue).
    /// </summary>
    public interface ICounterModel
    {
        ObservableProperty<int> Count { get; }

        void Increment(int amount);
    }
}
