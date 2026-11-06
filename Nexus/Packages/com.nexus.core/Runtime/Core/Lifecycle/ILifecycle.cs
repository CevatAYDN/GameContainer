using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Lifecycle interface for domain components that require synchronous startup notification.
    /// Executed during <see cref="Context"/> startup, after every <c>OnStartAsync</c>, for each singleton the context
    /// binds, whether or not something resolved it (a lazy service only once something has). An exception fails the
    /// startup.
    /// </summary>
    [Preserve]
    public interface IStartable
    {
        void Start();
    }

    /// <summary>
    /// Lifecycle interface for domain components that require asynchronous startup initialization.
    /// Executed during <see cref="Context"/> startup, after every <c>OnStartAsync</c>, for each singleton the context
    /// binds, whether or not something resolved it (a lazy service only once something has). An exception fails the
    /// startup.
    /// </summary>
    [Preserve]
    public interface IAsyncStartable
    {
        ValueTask StartAsync(CancellationToken ct);
    }

    /// <summary>
    /// Lifecycle interface for domain components that require synchronous teardown notification.
    /// Executed during <see cref="Context"/> disposal before singleton destruction.
    /// </summary>
    [Preserve]
    public interface IStoppable
    {
        void Stop();
    }

    /// <summary>
    /// Lifecycle interface for domain components that require asynchronous teardown cleanup.
    /// <see cref="Context.DisposeAsync"/> awaits it (up to 5 s) before singleton destruction. The synchronous
    /// <see cref="Context.Dispose"/> (Root.OnDestroy, NexusRuntime.Reset) does not block: it starts the stop, and the
    /// part after the first incomplete await runs while or after the singletons are disposed, so that part must not use
    /// injected services. A stop still running then is named in a warning (#291).
    /// </summary>
    [Preserve]
    public interface IAsyncStoppable
    {
        ValueTask StopAsync(CancellationToken ct);
    }
}
