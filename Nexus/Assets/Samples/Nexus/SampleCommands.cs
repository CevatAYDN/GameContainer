using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples
{
    [SignalHandler(typeof(SampleSignal))]
    public class SampleCommand : ICommand
    {
        public void Execute()
        {
            Debug.Log($"[Nexus] SampleCommand executed successfully with message: {Application.productName}");
        }
    }
}
