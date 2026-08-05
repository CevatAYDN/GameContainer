using System;
using System.Threading;

namespace Nexus.Core
{
    /// <summary>
    /// Shared mock context for use across test fixtures.
    /// Eliminates duplicated local mock context definitions.
    /// </summary>
    public class MockContext : IContext
    {
        public ISignalBus SignalBus { get; set; }
        public NexusDI Container { get; set; }
        public CancellationToken LifetimeToken => CancellationToken.None;
        public string ScopeTag { get; set; }
        public IContext Parent { get; set; }

        public MockContext(ISignalBus bus = null) { SignalBus = bus; }

        public void RegisterView(IView view) { }
        public void UnregisterView(IView view) { }

        /// <summary>
        /// Resolves via the assigned <see cref="Container"/>. Per the <see cref="IContext"/>
        /// contract, Resolve throws when the type cannot be resolved — use
        /// <see cref="TryResolve{T}()"/> for the null-returning variant.
        /// </summary>
        public T Resolve<T>() where T : class
        {
            if (Container != null) return Container.Resolve<T>();
            throw new InvalidOperationException(
                $"MockContext has no Container assigned — cannot resolve '{typeof(T).Name}'. " +
                "Assign a Container with the needed registrations, or use TryResolve for an optional lookup.");
        }
        public T TryResolve<T>() where T : class => null;
        public T TryResolve<T>(string name) where T : class => null;
        public T ResolveCrossBoundary<T>() where T : class => null;
        public void RegisterPlugin(INexusPlugin plugin) { }
        public void RemovePlugin(INexusPlugin plugin) { }
        public void Dispose() { }
    }
}
