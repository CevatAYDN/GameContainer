using System;

namespace Nexus.Samples.Counter
{
    public interface ICounterModel
    {
        int Count { get; }
        event Action<int> OnCountChanged;
        void Increment(int amount);
    }
}
