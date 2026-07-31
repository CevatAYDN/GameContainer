// Harness-side stubs for Nexus runtime types that are too Unity-coupled to compile
// standalone (Context, NexusRuntime, NexusTrace) but are referenced by the pure-C#
// benchmark path (SignalBus, HybridQueue, PluginSystem). These stubs are compile-time
// only — the benchmark never exercises the real Context/NexusRuntime behavior.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Core
{
    /// <summary>Resolves active contexts for cross-context broadcasting (defined in real NexusRuntime.cs).</summary>
    public interface IContextResolver
    {
        IReadOnlyList<IContext> GetActiveContexts();
    }

    /// <summary>Compile-time stand-in for the real Unity-coupled NexusRuntime.</summary>
    public static class NexusRuntime
    {
        public static Services.ILoggerService Logger => null;

        public static IContextResolver DefaultContextResolver { get; } = new DefaultResolver();

        public static void RegisterContext(IContext context) { }
        public static void UnregisterContext(IContext context) { }
        public static void Reset() { }

        private sealed class DefaultResolver : IContextResolver
        {
            public IReadOnlyList<IContext> GetActiveContexts() => Array.Empty<IContext>();
        }

        public static class Metrics
        {
            public static void RecordSignalDispatched() { }
            public static void RecordCommandExecuted() { }
            public static void RecordTrace(string label) { }
        }
    }

    /// <summary>Compile-time stand-in for the real Unity-coupled Context.</summary>
    public class Context : IContext
    {
        public ISignalBus SignalBus => SignalBusInternal;
        public SignalBus SignalBusInternal => null;
        public CancellationToken LifetimeToken => System.Threading.CancellationToken.None;
        public string ScopeTag => null;
        public IContext Parent => null;

        public bool HasInterceptors => false;
        public List<ContextPlugin> Plugins => new();
        public IReadOnlyList<ContextPlugin> PluginsReadOnlyCopy => Array.Empty<ContextPlugin>();

        public void IncrementInterceptorsCount() { }
        public void DecrementInterceptorsCount() { }

        public void RegisterView(IView view) { }
        public void UnregisterView(IView view) { }
        public T Resolve<T>() where T : class => null;
        public T TryResolve<T>() where T : class => null;
        public void RegisterPlugin(INexusPlugin plugin) { }
        public void RemovePlugin(INexusPlugin plugin) { }
        public void Dispose() { }
    }

    /// <summary>Compile-time stand-in for the ContextPlugin wrapper (defined in real Context.cs).</summary>
    public class ContextPlugin
    {
        public PluginContext context;
    }

    /// <summary>Compile-time stand-in for the trace sink interface (defined in Tracing/).</summary>
    public interface INexusTraceSink
    {
    }

    /// <summary>Compile-time stand-in for the tracing entry point (defined in Tracing/).</summary>
    public static class NexusTrace
    {
        public static void AddSink(INexusTraceSink sink) { }
        public static void RemoveSink(INexusTraceSink sink) { }
    }
}
