using UnityEngine;
using UnityEngine.InputSystem;

namespace Nexus.Capture
{
    /// <summary>
    /// Background driver that listens to keyboard shortcuts in Play Mode and invokes CaptureModeService.
    /// </summary>
    internal sealed class CaptureDriver : MonoBehaviour
    {
        private static CaptureDriver s_instance;

        private static readonly Key[] s_digitKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (s_instance != null) return;

            var go = new GameObject("NexusCaptureDriver");
            // Not DontSave: a DontSave object survives leaving Play Mode and leaks into edit mode.
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<CaptureDriver>();
        }

        private void Update()
        {
#if NEXUS_ADS
            CaptureAdSuppression.Tick();
#endif
            // StepFrame resolution: re-pauses on the next Update after a single frame step.
            if (CaptureModeService.IsSteppingFrame)
            {
                CaptureModeService.ResolveStepFrame();
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var settings = CaptureSettings.Load();

            // Toggle shortcut is active regardless of current capture state.
            if (keyboard[settings.ToggleKey].wasPressedThisFrame)
            {
                CaptureModeService.Toggle();
                return;
            }

            // All remaining shortcuts require Capture Mode to be open.
            if (!CaptureModeService.IsOpen) return;

            if (keyboard[settings.RecordKey].wasPressedThisFrame)
            {
                CaptureModeService.RequestRecordToggle();
            }

            if (keyboard[settings.HideUiKey].wasPressedThisFrame)
            {
                CaptureModeService.ToggleUi();
            }

            if (keyboard[settings.SlowerKey].wasPressedThisFrame)
            {
                CaptureModeService.Slower();
            }

            if (keyboard[settings.FasterKey].wasPressedThisFrame)
            {
                CaptureModeService.Faster();
            }

            if (keyboard[settings.PauseKey].wasPressedThisFrame)
            {
                CaptureModeService.TogglePause();
            }

            if (keyboard[settings.StepKey].wasPressedThisFrame)
            {
                CaptureModeService.StepFrame();
            }

            if (keyboard[settings.FreeCameraKey].wasPressedThisFrame)
            {
                CaptureModeService.RequestFreeCameraToggle();
            }

            if (keyboard[settings.GodModeKey].wasPressedThisFrame)
            {
                CaptureModeService.ToggleGodMode();
            }

            var hooks = CaptureModeService.Hooks;
            if (hooks != null && hooks.Presets != null)
            {
                int count = hooks.Presets.Count;
                for (int i = 0; i < s_digitKeys.Length; i++)
                {
                    if (keyboard[s_digitKeys[i]].wasPressedThisFrame)
                    {
                        if (i < count)
                        {
                            CaptureModeService.ApplyPreset(i);
                        }
                        break;
                    }
                }
            }
        }

        private void OnDestroy()
        {
            CaptureModeService.RestoreAll();
            if (s_instance == this)
            {
                s_instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            CaptureModeService.RestoreAll();
        }
    }
}
