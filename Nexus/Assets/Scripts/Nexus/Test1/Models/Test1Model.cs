using System;
using UnityEngine;

namespace Nexus
{
    public class TEST1Model : ITEST1Model
    {
        public int Counter { get; private set; }
        public event Action<int> OnCounterChanged;

        public void Increment(int amount)
        {
            Counter += amount;
            Debug.Log($"[{nameof(TEST1Model)}] Counter changed to: {Counter}");
            OnCounterChanged?.Invoke(Counter);
        }
    }
}
