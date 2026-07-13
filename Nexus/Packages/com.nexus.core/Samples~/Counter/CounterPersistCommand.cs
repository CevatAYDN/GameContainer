using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Bound with ExecutionMode.Exclusive (see CounterLifecycle).
    /// Exclusive guarantees a single handler for the signal — a safe single-writer
    /// for persistence. Demonstrates injecting a built-in Nexus service.
    /// </summary>
    public class CounterPersistCommand : ICommand<CounterPersistSignal>
    {
        [Inject] public ICounterModel Model { get; set; }
        [Inject] public IPlayerPrefsService Prefs { get; set; }

        public void Execute(CounterPersistSignal signal)
        {
            Prefs.SetInt("counter.sample.count", Model.Count.Value);
            Debug.Log($"[Counter] Persisted count = {Model.Count.Value} (Exclusive)");
        }
    }
}
