using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    // Command that handles the struct signal and updates the injected model
    public class Test1IncrementCommand : ICommand
    {
        [Inject] public ITest1Model Model { get; set; }
        [Inject] public Test1CounterSignal Signal { get; set; }

        public void Execute()
        {
            Debug.Log($"[{nameof(Test1IncrementCommand)}] Executing command with signal payload: {Signal.Value}");
            Model.Increment(Signal.Value);
        }
    }
}
