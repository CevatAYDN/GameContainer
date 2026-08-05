using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public static class NexusTestHarness
    {
        /// <summary>
        /// Upper bound for the synchronous <c>autoInitialize</c> bridge. Exceeding it throws
        /// instead of hanging the test runner forever.
        /// </summary>
        public static TimeSpan AutoInitializeTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public static NexusTestContext CreateContext()
        {
            var context = new Context(parent: null, contextData: null);
            return new NexusTestContext(context);
        }

        public static NexusTestContext CreateContext(string scopeTag)
        {
            var contextData = CreateContextData(scopeTag);
            var context = new Context(parent: null, contextData: contextData);
            return new NexusTestContext(context);
        }

        public static NexusTestContext CreateChildContext(NexusTestContext parent, string scopeTag = null)
        {
            var contextData = CreateContextData(scopeTag ?? "ChildContext");
            var context = new Context(parent: parent.Context, contextData: contextData);
            return new NexusTestContext(context);
        }

        /// <summary>
        /// Creates a test context with the provided configuration.
        /// </summary>
        /// <param name="configure">Configuration delegate that registers bindings, services, signals, etc.</param>
        /// <param name="autoInitialize">
        /// If true, runs the full initialization pipeline after Configure(): 
        /// InitializeReactiveModelsAsync → InitializeServicesAsync → lifecycle.OnInitializeAsync → lifecycle.OnStartAsync.
        /// Set to true for tests that use INexusService implementations requiring explicit InitializeAsync().
        /// Default false for backward compatibility with existing tests.
        /// </param>
        public static NexusTestContext CreateContext(Action<IContextBuilder> configure, bool autoInitialize = false)
        {
            return CreateContext(null, configure, autoInitialize);
        }

        public static NexusTestContext CreateContext(ContextData contextData, Action<IContextBuilder> configure, bool autoInitialize = false)
        {
            var context = new Context(parent: null, contextData: contextData);
            var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
            configure?.Invoke(builder);
            context.ConfigureWithBuilder(builder);

            if (autoInitialize)
            {
                RunBlocking(() => context.InitializeLifecycleAsync(context.ConfiguredLifecycles, default));
            }

            return new NexusTestContext(context);
        }

        /// <summary>
        /// Bridges the async lifecycle into this synchronous factory without deadlocking.
        /// A plain <c>GetAwaiter().GetResult()</c> hangs whenever a lifecycle step posts a
        /// continuation back to the calling thread's <see cref="SynchronizationContext"/>
        /// (Unity's main-thread context does exactly that): the thread is blocked, so the
        /// continuation never runs. Here the calling thread installs a pumping context and
        /// executes those continuations itself while it waits — the work still runs on the
        /// caller's thread, so lifecycle code may touch Unity APIs.
        /// </summary>
        private static void RunBlocking(Func<ValueTask> work)
        {
            var previous = SynchronizationContext.Current;
            var pump = new PumpingSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(pump);
            Task task;
            try
            {
                task = work().AsTask();
                // Stop pumping once the work finishes, whatever its outcome.
                task.ContinueWith(_ => pump.Complete(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                pump.Pump(AutoInitializeTimeout);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            if (!task.IsCompleted)
            {
                throw new TimeoutException(
                    $"NexusTestHarness auto-initialization did not complete within {AutoInitializeTimeout.TotalSeconds:0.#}s. " +
                    "Increase NexusTestHarness.AutoInitializeTimeout, or check the lifecycle for work that never completes.");
            }
            // Unwraps and rethrows the original exception with its stack intact.
            task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Minimal single-threaded <see cref="SynchronizationContext"/>: posted callbacks are
        /// queued and executed by <see cref="Pump"/> on the thread that installed it.
        /// </summary>
        private sealed class PumpingSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object State)> _queue = new();

            public override void Post(SendOrPostCallback d, object state)
            {
                if (d == null) return;
                try { _queue.Add((d, state)); }
                catch (InvalidOperationException)
                {
                    // Pumping already finished; run inline so the continuation is not lost.
                    d(state);
                }
            }

            public override void Send(SendOrPostCallback d, object state) => d?.Invoke(state);

            public void Complete()
            {
                try { _queue.CompleteAdding(); }
                catch (ObjectDisposedException) { }
            }

            /// <summary>Executes queued callbacks until completion is signalled or the deadline passes.</summary>
            public void Pump(TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (true)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return;
                    try
                    {
                        if (!_queue.TryTake(out var item, remaining)) return;
                        item.Callback(item.State);
                    }
                    catch (InvalidOperationException)
                    {
                        // CompleteAdding was called and the queue drained — work is done.
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Cleans up all active contexts registered during tests.
        /// Call this in a global test TearDown or OneTimeTearDown to ensure
        /// no leaked contexts interfere with subsequent tests.
        /// </summary>
        public static void CleanupAll()
        {
            NexusRuntime.Reset();
        }

        private static ContextData CreateContextData(string scopeTag)
        {
            var contextData = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            contextData.ScopeTag = scopeTag;
            contextData.AssemblyScopes = System.Array.Empty<string>();
            return contextData;
        }
    }
}
