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
        public CancellationToken LifetimeToken => CancellationToken.None;
        public string ScopeTag { get; set; }
        public IContext Parent { get; set; }

        public MockContext(ISignalBus bus = null) { SignalBus = bus; }

        public void RegisterView(IView view) { }
        public void UnregisterView(IView view) { }
        public T Resolve<T>() where T : class => null;
        public T TryResolve<T>() where T : class => null;
        public T TryResolve<T>(string name) where T : class => null;
        public T ResolveCrossBoundary<T>() where T : class => null;
        public void RegisterPlugin(INexusPlugin plugin) { }
        public void RemovePlugin(INexusPlugin plugin) { }
        public void Dispose() { }
    }
}
