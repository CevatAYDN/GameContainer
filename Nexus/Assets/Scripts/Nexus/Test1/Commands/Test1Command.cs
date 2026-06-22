using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    // Command that handles the struct signal and updates the injected model
    public class TEST1IncrementCommand : ICommand<TEST1CounterSignal>
    {
        [Inject] public ITEST1Model Model { get; set; }

        public void Execute(TEST1CounterSignal signal)
        {
            Debug.Log($"[{nameof(TEST1IncrementCommand)}] Executing command with signal payload: {signal.Value}");
            Model.Increment(signal.Value);
        }
    }
}
