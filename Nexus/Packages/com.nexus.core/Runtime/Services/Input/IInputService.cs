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
    /// Pluggable input provider interface. Implement to feed custom input from
    /// Unity New Input System (InputActionAsset), Rewired, Touch Gestures, or Network Replay.
    /// </summary>
    [Preserve]
    public interface IInputProvider
    {
        Vector2 GetMoveInput();
        bool GetButton(string actionName);
        bool GetButtonDown(string actionName);
        bool GetButtonUp(string actionName);
    }

    /// <summary>
    /// Service contract for mobile Virtual Joystick and Desktop Keyboard/Mouse/Touch/Gamepad input.
    /// Bridges input directly to Nexus SignalBus without per-frame allocations.
    /// Supports pluggable <see cref="IInputProvider"/> for Unity New Input System or custom frameworks.
    /// </summary>
    [Preserve]
    public interface IInputService
    {
        Vector2 MoveInput { get; }
        bool IsInputActive { get; }
        void SetVirtualJoystickInput(Vector2 direction);
        void SetInputProvider(IInputProvider provider);
        bool GetButton(string actionName);
        bool GetButtonDown(string actionName);
        bool GetButtonUp(string actionName);
        void UpdateInput(float deltaTime);
    }
}
