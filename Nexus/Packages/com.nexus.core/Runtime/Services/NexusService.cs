using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for Nexus services. Provides automatic injection of
    /// <see cref="IContext"/> and <see cref="ISignalBus"/> so derived services
    /// can interact with the Nexus ecosystem without manual DI lookups.
    ///
    /// Usage:
    /// <code>
    /// public interface IPlayerPersistenceService : INexusService { ... }
    /// public class PlayerPersistenceService : NexusService&lt;IPlayerPersistenceService&gt;, IPlayerPersistenceService
    /// {
    ///     public override async ValueTask InitializeAsync(CancellationToken ct)
    ///     {
    ///         // load save data
    ///     }
    /// }
    /// </code>
    /// Register in lifecycle:
    /// <code>
    /// builder.BindService&lt;IPlayerPersistenceService, PlayerPersistenceService&gt;();
    /// </code>
    /// </summary>
    [Preserve]
    public abstract class NexusService<T> : INexusService where T : class
    {
        /// <summary>The owning Nexus context. Automatically injected.</summary>
        [Inject] public IContext Context { get; protected set; }

        /// <summary>The context's signal bus for firing/dispatching signals.</summary>
        [Inject] public ISignalBus SignalBus { get; protected set; }

        public virtual ValueTask InitializeAsync(CancellationToken ct) => default;
        public virtual void OnDispose() { }
    }
}
