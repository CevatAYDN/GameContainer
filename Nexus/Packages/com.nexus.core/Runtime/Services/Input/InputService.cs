using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Default <see cref="IInputService"/>: publishes the virtual joystick vector and, on
    /// desktop, an optional keyboard fallback.
    ///
    /// The service ticks itself — it registers with <see cref="ITickService"/> during
    /// initialization, so <see cref="UpdateInput"/> is driven automatically. Previously the
    /// caller had to pump it manually with no indication anywhere that it was required, and a
    /// registered-but-unpumped service silently reported no input at all.
    /// </summary>
    [Preserve]
    public class InputService : NexusService<IInputService>, IInputService, ITickable
    {
        [OptionalInject] public ITickService TickService { get; set; }
        [OptionalInject] public IInputProvider InputProvider { get; set; }

        private Vector2 _virtualJoystickInput;
        private Vector2 _currentMoveInput;
        private IInputProvider _customProvider;

        public Vector2 MoveInput => _currentMoveInput;
        public bool IsInputActive => _currentMoveInput.sqrMagnitude > 0.001f;

        /// <summary>
        /// When true (default on desktop), an unset joystick falls back to the legacy
        /// <c>Horizontal</c>/<c>Vertical</c> input axes if New Input System is unavailable.
        /// </summary>
        public bool EnableLegacyKeyboardFallback { get; set; } =
#if UNITY_EDITOR || UNITY_STANDALONE
            true;
#else
            false;
#endif

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            TickService?.RegisterTickable(this);
            return default;
        }

        public override void OnDispose()
        {
            TickService?.UnregisterTickable(this);
            base.OnDispose();
        }

        public void Tick(float deltaTime) => UpdateInput(deltaTime);

        public void SetInputProvider(IInputProvider provider)
        {
            _customProvider = provider;
        }

        public void SetVirtualJoystickInput(Vector2 direction)
        {
            _virtualJoystickInput = Vector2.ClampMagnitude(direction, 1f);
        }

        public bool GetButton(string actionName)
        {
            var provider = _customProvider ?? InputProvider;
            if (provider != null) return provider.GetButton(actionName);
#if UNITY_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Pressed)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                try { return Input.GetButton(actionName); } catch { }
            }
            return false;
        }

        public bool GetButtonDown(string actionName)
        {
            var provider = _customProvider ?? InputProvider;
            if (provider != null) return provider.GetButtonDown(actionName);
#if UNITY_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Down)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                try { return Input.GetButtonDown(actionName); } catch { }
            }
            return false;
        }

        public bool GetButtonUp(string actionName)
        {
            var provider = _customProvider ?? InputProvider;
            if (provider != null) return provider.GetButtonUp(actionName);
#if UNITY_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Up)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                try { return Input.GetButtonUp(actionName); } catch { }
            }
            return false;
        }

        public void UpdateInput(float deltaTime)
        {
            Vector2 input = _virtualJoystickInput;
            var provider = _customProvider ?? InputProvider;

            if (provider != null)
            {
                var pInput = provider.GetMoveInput();
                if (pInput.sqrMagnitude > 0.001f)
                    input = pInput;
            }
#if UNITY_INPUT_SYSTEM
            if (input.sqrMagnitude < 0.001f && Application.isPlaying)
            {
                var nisInput = ReadNewInputSystem();
                if (nisInput.sqrMagnitude > 0.001f)
                    input = nisInput;
            }
#endif
            if (input.sqrMagnitude < 0.001f && EnableLegacyKeyboardFallback && Application.isPlaying)
            {
                input = ReadLegacyAxes();
            }

            _currentMoveInput = input;

            if (IsInputActive && SignalBus != null)
            {
                SignalBus.Fire(new PlayerMoveSignal(_currentMoveInput));
            }
        }

#if UNITY_INPUT_SYSTEM
        private enum ButtonQuery { Pressed, Down, Up }

        private static bool CheckNewInputButton(string actionName, ButtonQuery query)
        {
            if (!Application.isPlaying) return false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    if (string.Equals(actionName, "Jump", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(actionName, "Submit", StringComparison.OrdinalIgnoreCase))
                    {
                        return query switch
                        {
                            ButtonQuery.Pressed => kb.spaceKey.isPressed || kb.enterKey.isPressed,
                            ButtonQuery.Down => kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame,
                            ButtonQuery.Up => kb.spaceKey.wasReleasedThisFrame || kb.enterKey.wasReleasedThisFrame,
                            _ => false
                        };
                    }
                    if (string.Equals(actionName, "Cancel", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(actionName, "Pause", StringComparison.OrdinalIgnoreCase))
                    {
                        return query switch
                        {
                            ButtonQuery.Pressed => kb.escapeKey.isPressed,
                            ButtonQuery.Down => kb.escapeKey.wasPressedThisFrame,
                            ButtonQuery.Up => kb.escapeKey.wasReleasedThisFrame,
                            _ => false
                        };
                    }
                }
            }
            catch { }
            return false;
        }

        private static Vector2 ReadNewInputSystem()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    float x = 0f;
                    float y = 0f;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
                    if (x != 0f || y != 0f) return new Vector2(x, y).normalized;
                }

                var gp = UnityEngine.InputSystem.Gamepad.current;
                if (gp != null)
                {
                    var stick = gp.leftStick.ReadValue();
                    if (stick.sqrMagnitude > 0.001f) return stick;
                }
            }
            catch { }
            return Vector2.zero;
        }
#endif

        private bool _legacyAxesUnavailable;

        private Vector2 ReadLegacyAxes()
        {
            if (_legacyAxesUnavailable) return Vector2.zero;
            try
            {
                float h = Input.GetAxisRaw("Horizontal");
                float v = Input.GetAxisRaw("Vertical");
                return new Vector2(h, v).normalized;
            }
            catch (Exception ex)
            {
                // Thrown when the project disabled the legacy input backend. Report once and
                // stop probing instead of throwing every frame.
                _legacyAxesUnavailable = true;
                NexusRuntime.Logger?.LogWarning(
                    $"[InputService] Legacy input axes are unavailable ({ex.GetType().Name}); keyboard fallback disabled. " +
                    "Feed input through SetVirtualJoystickInput, SetInputProvider, or New Input System.");
                return Vector2.zero;
            }
        }
    }
}
