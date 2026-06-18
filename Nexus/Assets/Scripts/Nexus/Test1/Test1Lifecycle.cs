using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{
    // Automatically discovered and bound by Nexus based on naming convention (Test1Lifecycle).
    // No need to attach this to any GameObject!
    public class Test1Lifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            Debug.Log($"[{nameof(Test1Lifecycle)}] Configuring architecture layers...");

            // 1. Bind Observable/Reactive Model
            builder.BindModel<ITest1Model, Test1Model>();

            // 2. Bind Command that reacts to the struct signal
            builder.BindCommand<Test1CounterSignal, Test1IncrementCommand>();
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
            Debug.Log($"[{nameof(Test1Lifecycle)}] Context disposed.");
        }
    }
}
