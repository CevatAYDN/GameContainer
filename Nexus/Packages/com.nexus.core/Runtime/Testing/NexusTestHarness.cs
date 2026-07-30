using System;

namespace Nexus.Core
{
    public static class NexusTestHarness
    {
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
            var context = new Context(parent: null, contextData: null);
            var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
            configure?.Invoke(builder);
            context.Configure();

            if (autoInitialize)
            {
                context.InitializeLifecycleAsync(context.ConfiguredLifecycles, default).GetAwaiter().GetResult();
            }

            return new NexusTestContext(context);
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
