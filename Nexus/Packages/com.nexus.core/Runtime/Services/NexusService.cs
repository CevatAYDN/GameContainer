using System;
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
    public abstract class NexusService<T> : INexusService, IDisposable where T : class
    {
        /// <summary>
        /// The owning Nexus context. Injected by the DI container — do not set manually.
        /// </summary>
        [Inject] public IContext Context { get; protected set; }

        /// <summary>
        /// The context's signal bus for firing/dispatching signals.
        /// Injected by the DI container — do not set manually.
        /// </summary>
        [Inject] public ISignalBus SignalBus { get; protected set; }

        public virtual ValueTask InitializeAsync(CancellationToken ct) => default;

        /// <summary>
        /// Delegates to <see cref="Dispose"/> so services that only override
        /// <c>Dispose()</c> do not need to repeat <c>OnDispose() => Dispose()</c>.
        /// Override this directly if you need custom cleanup before <c>Dispose()</c>.
        /// </summary>
        public virtual void OnDispose() => Dispose();

        /// <summary>
        /// Override to provide custom cleanup logic.
        /// Called automatically by <see cref="OnDispose"/>.
        /// </summary>
        public virtual void Dispose() { }
    }
}
