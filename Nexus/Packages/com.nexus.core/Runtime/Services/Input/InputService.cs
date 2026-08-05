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

        private Vector2 _virtualJoystickInput;
        private Vector2 _currentMoveInput;

        public Vector2 MoveInput => _currentMoveInput;
        public bool IsInputActive => _currentMoveInput.sqrMagnitude > 0.001f;

        /// <summary>
        /// When true (default on desktop), an unset joystick falls back to the legacy
        /// <c>Horizontal</c>/<c>Vertical</c> input axes. Turn it off for projects using the
        /// Input System package exclusively — the legacy axis API throws when the project's
        /// Active Input Handling is set to "Input System Package (New)".
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

        public void SetVirtualJoystickInput(Vector2 direction)
        {
            _virtualJoystickInput = Vector2.ClampMagnitude(direction, 1f);
        }

        public void UpdateInput(float deltaTime)
        {
            Vector2 input = _virtualJoystickInput;

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
                    "Feed input through SetVirtualJoystickInput, or set EnableLegacyKeyboardFallback = false to silence this.");
                return Vector2.zero;
            }
        }
    }
}
