using UnityEngine;
using UnityEngine.InputSystem;

namespace Nexus.Capture
{
    /// <summary>
    /// Smooth free camera mover driving translation and rotation in unscaled time.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class FreeCameraMover : MonoBehaviour
    {
        private const float MinSpeed = 0.5f;
        private const float MaxSpeed = 50f;
        private const float DefaultSpeed = 5f;
        private const float MoveSharpness = 12f;
        private const float LookSharpness = 20f;
        private const float LookSensitivity = 0.1f;

        private float _baseSpeed = DefaultSpeed;
        private Vector3 _currentVelocity;

        private float _targetYaw;
        private float _targetPitch;
        private float _currentYaw;
        private float _currentPitch;

        private void OnEnable()
        {
            var angles = transform.eulerAngles;
            _targetYaw = _currentYaw = angles.y;
            float pitch = angles.x;
            if (pitch > 180f) pitch -= 360f;
            _targetPitch = _currentPitch = Mathf.Clamp(pitch, -89f, 89f);
        }

        private void LateUpdate()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard == null || mouse == null) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            // Scroll wheel adjusts base movement speed.
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                float step = scroll > 0f ? 1f : -1f;
                _baseSpeed = Mathf.Clamp(_baseSpeed + step, MinSpeed, MaxSpeed);
            }

            // Right mouse button held: look with yaw and pitch clamped to +/-89 degrees.
            if (mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _targetYaw += delta.x * LookSensitivity;
                _targetPitch -= delta.y * LookSensitivity;
                _targetPitch = Mathf.Clamp(_targetPitch, -89f, 89f);
            }

            float lookT = 1f - Mathf.Exp(-LookSharpness * dt);
            _currentYaw = Mathf.Lerp(_currentYaw, _targetYaw, lookT);
            _currentPitch = Mathf.Lerp(_currentPitch, _targetPitch, lookT);
            transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);

            // WASD along local camera axes, Q down / E up in world space.
            Vector3 moveDir = Vector3.zero;
            if (keyboard.wKey.isPressed) moveDir += transform.forward;
            if (keyboard.sKey.isPressed) moveDir -= transform.forward;
            if (keyboard.dKey.isPressed) moveDir += transform.right;
            if (keyboard.aKey.isPressed) moveDir -= transform.right;
            if (keyboard.eKey.isPressed) moveDir += Vector3.up;
            if (keyboard.qKey.isPressed) moveDir -= Vector3.up;

            if (moveDir.sqrMagnitude > 1f)
            {
                moveDir.Normalize();
            }

            float multiplier = (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) ? 4f : 1f;
            Vector3 targetVelocity = moveDir * (_baseSpeed * multiplier);

            float moveT = 1f - Mathf.Exp(-MoveSharpness * dt);
            _currentVelocity = Vector3.Lerp(_currentVelocity, targetVelocity, moveT);
            transform.position += _currentVelocity * dt;
        }
    }
}
