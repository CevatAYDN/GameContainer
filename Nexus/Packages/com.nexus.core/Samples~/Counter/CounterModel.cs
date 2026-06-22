using System;

namespace Nexus.Samples.Counter
{
    public class CounterModel : ICounterModel
    {
        public int Count { get; private set; }
        public event Action<int> OnCountChanged;

        public void Increment(int amount)
        {
            Count += amount;
            OnCountChanged?.Invoke(Count);
        }
    }
}
