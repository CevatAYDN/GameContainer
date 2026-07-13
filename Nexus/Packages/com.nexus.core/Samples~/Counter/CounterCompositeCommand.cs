using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Composite (fan-in) trigger. Fires only after BOTH CounterAckSignal and
    /// CounterDataSignal have been received. Registered automatically by the
    /// runtime via [CompositeSignalHandler] — no explicit BindCommand is needed.
    /// Implements the non-generic ICommand (Execute() with no args).
    /// </summary>
    [CompositeSignalHandler(typeof(CounterAckSignal), typeof(CounterDataSignal))]
    public class CounterCompositeCommand : ICommand
    {
        [Inject] public ICounterModel Model { get; set; }

        public void Execute()
        {
            Debug.Log($"[Counter] Composite trigger fired — both ack + data received (count={Model.Count.Value})");
        }
    }
}
