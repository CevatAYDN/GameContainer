using System;

namespace Nexus.Core
{
    public interface IResettable
    {
        void Reset();
    }

    /// <summary>
    /// Model that can be reset to its initial state (e.g. for pooling or replay).
    /// Memory Ownership Model.
    /// Duplicate of <see cref="IResettable"/>; kept (inheriting it) for API compatibility.
    /// </summary>
    [Obsolete("IResettableModel duplicates IResettable — implement IResettable instead. This interface now inherits it and will be removed in a future major version.")]
    public interface IResettableModel : IResettable
    {
    }

    /// <summary>
    /// Model with explicit dispose lifecycle. BuildValidation checks that
    /// all IDisposableModel instances are disposed in the disposal chain.
    /// Memory Ownership Model.
    /// </summary>
    public interface IDisposableModel : IDisposable
    {
    }

    /// <summary>
    /// Defines a model that supports saving and restoring its internal state snapshot.
    /// Used by NetworkSignalBus to handle deterministic rollback and state recovery in multiplayer games.
    /// </summary>
    public interface ISnapshotableModel<TState> where TState : struct
    {
        TState CaptureSnapshot();
        void RestoreSnapshot(TState snapshot);
    }
}
