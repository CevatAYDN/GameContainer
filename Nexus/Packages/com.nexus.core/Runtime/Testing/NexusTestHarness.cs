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

        public static NexusTestContext CreateContext(Action<IContextBuilder> configure)
        {
            var context = new Context(parent: null, contextData: null);
            var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
            configure?.Invoke(builder);
            context.Configure();
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
