using System.Threading;
using Nexus.Core;

namespace Nexus.Tests
{
    /// <summary>
    /// Shared mock context for use across test fixtures.
    /// Eliminates the duplicated MockContext class that previously existed in each test file.
    /// </summary>
    public class MockContext : IContext
    {
        public ISignalBus SignalBus => null;
        public CancellationToken LifetimeToken => CancellationToken.None;
        public string ScopeTag => null;
        public IContext Parent => null;
        public void RegisterView(IView view) { }
        public void UnregisterView(IView view) { }
        public T Resolve<T>() where T : class => null;
        public void RegisterPlugin(INexusPlugin plugin) { }
        public void RemovePlugin(INexusPlugin plugin) { }
        public void Dispose() { }
    }
}
