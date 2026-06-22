using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    // Automatically discovered and bound by Nexus based on naming convention (TEST1Lifecycle).
    // No need to attach this to any GameObject!
    public class TEST1Lifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            Debug.Log($"[{nameof(TEST1Lifecycle)}] Configuring architecture layers...");

            // 1. Bind Observable/Reactive Model
            builder.BindModel<ITEST1Model, TEST1Model>();

            // 2. Bind Command that reacts to the struct signal
            builder.BindCommand<TEST1CounterSignal, TEST1IncrementCommand>();
        }

        public ValueTask OnInitializeAsync(CancellationToken ct)
        {
            // Async initialization logic
            return default;
        }

        public ValueTask OnStartAsync(CancellationToken ct)
        {
            // Start logic (executed after initialization)
            return default;
        }

        public void OnDispose()
        {
            Debug.Log($"[{nameof(TEST1Lifecycle)}] Context disposed.");
        }
    }
}
