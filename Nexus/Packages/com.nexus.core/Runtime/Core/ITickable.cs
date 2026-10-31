using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Interface for classes that require per-frame updates without deriving from MonoBehaviour.
    /// Managed by <see cref="Services.ITickService"/>.
    /// </summary>
    [Preserve]
    public interface ITickable
    {
        void Tick(float deltaTime);
    }

    /// <summary>
    /// A tickable that keeps ticking while the <see cref="Services.ITickService"/> is paused (its flag or a
    /// <see cref="Services.ITickService.HoldPause"/> handle), for work that runs on real time and must not wait for a
    /// screen or an ad to close, such as the save throttler (issue #202). Gameplay stays an <see cref="ITickable"/>.
    /// </summary>
    [Preserve]
    public interface IUnpausableTickable : ITickable
    {
    }

    /// <summary>
    /// Interface for classes that require physics/fixed-rate updates.
    /// </summary>
    [Preserve]
    public interface IFixedTickable
    {
        void FixedTick(float fixedDeltaTime);
    }

    /// <summary>
    /// Interface for classes that require late-frame updates (e.g. camera, UI follow).
    /// </summary>
    [Preserve]
    public interface ILateTickable
    {
        void LateTick(float deltaTime);
    }
}
