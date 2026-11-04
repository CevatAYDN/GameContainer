using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Lightweight struct carrying player move direction signal.
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
    /// A provider answers the movement and every button read; one that supplies only some buttons
    /// implements <see cref="IPartialInputProvider"/>.
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
    /// A provider that supplies only some buttons, such as an on-screen joystick that has none. The
    /// service reads a button the provider does not supply itself (Input System or Input Manager), so
    /// Android's back key still reaches "Cancel". The movement stays the provider's, neutral frames
    /// included (issue #156).
    /// </summary>
    [Preserve]
    public interface IPartialInputProvider : IInputProvider
    {
        /// <summary>True when the provider answers <paramref name="actionName"/>; false leaves it to the service.</summary>
        bool ProvidesButton(string actionName);
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
