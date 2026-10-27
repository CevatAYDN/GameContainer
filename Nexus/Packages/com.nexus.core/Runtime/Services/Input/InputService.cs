using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            CreateButtonActions();
#endif
            return default;
        }

        public override void OnDispose()
        {
            TickService?.UnregisterTickable(this);
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            _cancelAction?.Dispose();
            _submitAction?.Dispose();
            _cancelAction = _submitAction = null;
#endif
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
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Pressed)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                return ReadLegacyButton(actionName, 0);
            }
            return false;
        }

        public bool GetButtonDown(string actionName)
        {
            var provider = _customProvider ?? InputProvider;
            if (provider != null) return provider.GetButtonDown(actionName);
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Down)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                return ReadLegacyButton(actionName, 1);
            }
            return false;
        }

        public bool GetButtonUp(string actionName)
        {
            var provider = _customProvider ?? InputProvider;
            if (provider != null) return provider.GetButtonUp(actionName);
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (CheckNewInputButton(actionName, ButtonQuery.Up)) return true;
#endif
            if (EnableLegacyKeyboardFallback && Application.isPlaying && !_legacyAxesUnavailable)
            {
                return ReadLegacyButton(actionName, 2);
            }
            return false;
        }

        public void UpdateInput(float deltaTime)
        {
            Vector2 input = _virtualJoystickInput;
            var provider = _customProvider ?? InputProvider;

            if (provider != null)
            {
                // A provider owns the complete input stream, including neutral frames.
                // Replay/cutscene providers must not inherit stale joystick or live keys.
                input = Vector2.ClampMagnitude(provider.GetMoveInput(), 1f);
            }
#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (provider == null && input.sqrMagnitude < 0.001f && Application.isPlaying)
            {
                var nisInput = ReadNewInputSystem();
                if (nisInput.sqrMagnitude > 0.001f)
                    input = nisInput;
            }
#endif
            if (provider == null && input.sqrMagnitude < 0.001f && EnableLegacyKeyboardFallback && Application.isPlaying)
            {
                input = ReadLegacyAxes();
            }

            _currentMoveInput = input;

            if (IsInputActive && SignalBus != null)
            {
                SignalBus.Fire(new PlayerMoveSignal(_currentMoveInput));
            }
        }

#if UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        private enum ButtonQuery { Pressed, Down, Up }

        // The legacy path logs its first failure; this path swallowed every one, so a broken Input System setup read as "no input".
        private static bool s_inputSystemFailureReported;

        private static void ReportInputSystemFailure(Exception ex)
        {
            if (s_inputSystemFailureReported) return;
            s_inputSystemFailureReported = true;
            NexusRuntime.Logger?.LogWarning($"[InputService] Input System read failed ({ex.GetType().Name}: {ex.Message}); input reads as none. Check the Input System setup or supply IInputProvider.");
        }

        // Actions, not a key's wasPressedThisFrame: an action records every event, so a press and release in one update (Android's
        // back key) still counts, and its binding covers a keyboard that appears after the service starts (#92).
        private InputAction _cancelAction;
        private InputAction _submitAction;

        private void CreateButtonActions()
        {
            // Reads report nothing outside play mode, so edit-mode runs keep no enabled actions.
            if (!Application.isPlaying) return;
            try
            {
                _cancelAction = new InputAction("Cancel", InputActionType.Button, "<Keyboard>/escape");
                _submitAction = new InputAction("Submit", InputActionType.Button, "<Keyboard>/space");
                _submitAction.AddBinding("<Keyboard>/enter");
                _cancelAction.Enable();
                _submitAction.Enable();
            }
            catch (Exception ex) { ReportInputSystemFailure(ex); }
        }

        private bool CheckNewInputButton(string actionName, ButtonQuery query)
        {
            if (!Application.isPlaying) return false;
            InputAction action = null;
            if (string.Equals(actionName, "Jump", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "Submit", StringComparison.OrdinalIgnoreCase))
                action = _submitAction;
            else if (string.Equals(actionName, "Cancel", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(actionName, "Pause", StringComparison.OrdinalIgnoreCase))
                action = _cancelAction;
            if (action == null) return false;
            try
            {
                return query switch
                {
                    ButtonQuery.Pressed => action.IsPressed(),
                    ButtonQuery.Down => action.WasPressedThisFrame(),
                    ButtonQuery.Up => action.WasReleasedThisFrame(),
                    _ => false
                };
            }
            catch (Exception ex) { ReportInputSystemFailure(ex); }
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
            catch (Exception ex) { ReportInputSystemFailure(ex); }
            return Vector2.zero;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        private System.Collections.Generic.HashSet<string> _unavailableLegacyButtons;
#endif

        private bool ReadLegacyButton(string actionName, int phase)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (string.IsNullOrEmpty(actionName) || _unavailableLegacyButtons?.Contains(actionName) == true) return false;
            try
            {
                return phase switch
                {
                    0 => Input.GetButton(actionName),
                    1 => Input.GetButtonDown(actionName),
                    _ => Input.GetButtonUp(actionName)
                };
            }
            catch (Exception ex)
            {
                _unavailableLegacyButtons ??= new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                _unavailableLegacyButtons.Add(actionName);
                NexusRuntime.Logger?.LogWarning($"[InputService] Legacy button '{actionName}' unavailable ({ex.GetType().Name}); configure the action or supply IInputProvider.");
            }
#endif
            return false;
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        private bool _legacyAxesUnavailable;
#else
        private const bool _legacyAxesUnavailable = false;
#endif

        private Vector2 ReadLegacyAxes()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
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
#else
            return Vector2.zero;
#endif
        }
    }
}
