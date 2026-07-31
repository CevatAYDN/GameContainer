using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    [Preserve]
    public class InputService : NexusService<IInputService>, IInputService
    {
        private Vector2 _virtualJoystickInput;
        private Vector2 _currentMoveInput;

        public Vector2 MoveInput => _currentMoveInput;
        public bool IsInputActive => _currentMoveInput.sqrMagnitude > 0.001f;

        public void SetVirtualJoystickInput(Vector2 direction)
        {
            _virtualJoystickInput = Vector2.ClampMagnitude(direction, 1f);
        }

        public void UpdateInput(float deltaTime)
        {
            Vector2 input = _virtualJoystickInput;

            // Keyboard fallback in Editor or PC Standalone
#if UNITY_EDITOR || UNITY_STANDALONE
            if (input.sqrMagnitude < 0.001f && Application.isPlaying)
            {
                float h = Input.GetAxisRaw("Horizontal");
                float v = Input.GetAxisRaw("Vertical");
                input = new Vector2(h, v).normalized;
            }
#endif

            _currentMoveInput = input;

            if (IsInputActive && SignalBus != null)
            {
                SignalBus.Fire(new PlayerMoveSignal(_currentMoveInput));
            }
        }
    }
}
