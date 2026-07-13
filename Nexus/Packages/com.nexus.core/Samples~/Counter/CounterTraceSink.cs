using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Causal-tracing sink. Receives every signal/command/model-change event in
    /// the execution chain. Registered via NexusTrace.AddSink(...) in
    /// CounterLifecycle.OnStartAsync.
    /// </summary>
    public class CounterTraceSink : INexusTraceSink
    {
        public void Write(in TraceEvent traceEvent)
        {
            Debug.Log($"[Trace] {traceEvent.Type} {traceEvent.TypeName} ({traceEvent.Status})");
        }
    }
}
