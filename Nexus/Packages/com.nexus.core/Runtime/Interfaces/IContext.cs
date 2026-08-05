using System;
using System.Threading;

namespace Nexus.Core
{
    /// <summary>
    /// Resolution-only view of a context. Depend on this instead of <see cref="IContext"/>
    /// wherever code only needs to pull dependencies — it keeps consumers off the view,
    /// plugin and lifetime surfaces, and it is trivial to substitute in tests.
    /// </summary>
    public interface IResolver
    {
        T Resolve<T>() where T : class;
        T TryResolve<T>() where T : class;
        /// <summary>Safely resolves a named binding, or null if not registered.</summary>
        T TryResolve<T>(string name) where T : class;

        /// <summary>
        /// Resolves a dependency by walking UP the parent-context chain.
        /// Searches the current context first, then parent, then grandparent, etc.
        /// This is the explicit opt-in equivalent of StrangeIoC's <c>crossContextInjectionBinder</c>.
        /// Types must be registered via <see cref="IContextBuilder.BindCrossBoundary{TInterface,TImplementation}"/>
        /// in the owning context.
        /// </summary>
        T ResolveCrossBoundary<T>() where T : class;
    }

    /// <summary>Registration surface for views bound to a context.</summary>
    public interface IViewRegistry
    {
        void RegisterView(IView view);
        void UnregisterView(IView view);
    }

    /// <summary>Registration surface for context plugins.</summary>
    public interface IPluginRegistry
    {
        void RegisterPlugin(INexusPlugin plugin);
        void RemovePlugin(INexusPlugin plugin);
    }

    public interface IContext : IResolver, IViewRegistry, IPluginRegistry, IDisposable
    {
        ISignalBus SignalBus { get; }

        /// <summary>
        /// The backing DI container. This is deliberately the concrete <see cref="NexusDI"/>
        /// type — the container is Nexus's own implementation detail, not an extension point,
        /// and exposing it as an interface would imply substitutability that the framework
        /// does not support (use <see cref="IDependencyAdapter"/> to bridge a foreign
        /// container instead). Application code should depend on <see cref="IResolver"/>
        /// rather than reaching through this property.
        /// </summary>
        NexusDI Container { get; }
        CancellationToken LifetimeToken { get; }
        string ScopeTag { get; }
        IContext Parent { get; }
    }
}
