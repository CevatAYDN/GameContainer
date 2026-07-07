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
        public ISignalBus SignalBus => null;
        public CancellationToken LifetimeToken => CancellationToken.None;
        public string ScopeTag => null;
        public IContext Parent => null;
        public bool HasInterceptors => false;
        public System.Collections.Generic.IReadOnlyList<(INexusPlugin plugin, IContext context)> PluginsReadOnlyCopy => Array.Empty<(INexusPlugin, IContext)>();
        public System.Collections.Generic.IReadOnlyList<ISignalInterceptor> Interceptors => Array.Empty<ISignalInterceptor>();

        public void RegisterView(IView view) { }
        public void UnregisterView(IView view) { }
        public T Resolve<T>() where T : class => null;
        public T TryResolve<T>() where T : class => null;
        public void RegisterPlugin(INexusPlugin plugin) { }
        public void RemovePlugin(INexusPlugin plugin) { }
        public void Dispose() { }
    }
}
