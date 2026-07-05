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
