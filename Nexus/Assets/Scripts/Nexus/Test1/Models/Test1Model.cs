using System;
using UnityEngine;

namespace Nexus
{
    public class Test1Model : ITest1Model
    {
        public int Counter { get; private set; }
        public event Action<int> OnCounterChanged;

        public void Increment(int amount)
        {
            Counter += amount;
            Debug.Log($"[{nameof(Test1Model)}] Counter changed to: {Counter}");
            OnCounterChanged?.Invoke(Counter);
        }
    }
}
