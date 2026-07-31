using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Lightweight 0-GC struct carrying player move direction signal.
    /// </summary>
    [Preserve]
    public readonly struct PlayerMoveSignal
    {
        public readonly Vector2 Direction;
        public PlayerMoveSignal(Vector2 direction) => Direction = direction;
    }

    /// <summary>
    /// Service contract for mobile Virtual Joystick and Desktop Keyboard/Mouse/Touch input.
    /// Bridges mobile input directly to Nexus SignalBus without per-frame allocations.
    /// </summary>
    [Preserve]
    public interface IInputService
    {
        Vector2 MoveInput { get; }
        bool IsInputActive { get; }
        void SetVirtualJoystickInput(Vector2 direction);
        void UpdateInput(float deltaTime);
    }
}
