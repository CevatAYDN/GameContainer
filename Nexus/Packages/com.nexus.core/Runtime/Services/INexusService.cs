using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Defines a managed Nexus service with explicit lifecycle.
    /// Services are registered via IContextBuilder.BindService and are automatically
    /// initialized after configuration and disposed on context shutdown.
    /// </summary>
    [Preserve]
    public interface INexusService
    {
        /// <summary>
        /// Called once after all configuration bindings are resolved.
        /// Use for async initialization (loading assets, connecting to services, etc.).
        /// </summary>
        ValueTask InitializeAsync(CancellationToken ct);

        /// <summary>
        /// Called when the owning context is disposed.
        /// Use for cleanup (disposing connections, saving state, etc.).
        /// </summary>
        void OnDispose();
    }
}
