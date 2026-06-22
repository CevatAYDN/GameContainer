using System;

namespace Nexus
{
    public interface ITEST1Model
    {
        int Counter { get; }
        event Action<int> OnCounterChanged;
        void Increment(int amount);
    }
}
