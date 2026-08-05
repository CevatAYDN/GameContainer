using System;
using System.Threading;

namespace Nexus.Core
{
    public interface IContext : IDisposable
    {
        ISignalBus SignalBus { get; }
        NexusDI Container { get; }
        CancellationToken LifetimeToken { get; }
        string ScopeTag { get; }
        void RegisterView(IView view);
        void UnregisterView(IView view);
        T Resolve<T>() where T : class;
        T TryResolve<T>() where T : class;
        /// <summary>Safely resolves a named binding, or null if not registered.</summary>
        T TryResolve<T>(string name) where T : class;
        IContext Parent { get; }

        /// <summary>
        /// Resolves a dependency by walking UP the parent-context chain.
        /// Searches the current context first, then parent, then grandparent, etc.
        /// This is the explicit opt-in equivalent of StrangeIoC's <c>crossContextInjectionBinder</c>.
        /// Types must be registered via <see cref="IContextBuilder.BindCrossBoundary{TInterface,TImplementation}"/>
        /// in the owning context.
        /// </summary>
        T ResolveCrossBoundary<T>() where T : class;
        void RegisterPlugin(INexusPlugin plugin);
        void RemovePlugin(INexusPlugin plugin);
    }
}
