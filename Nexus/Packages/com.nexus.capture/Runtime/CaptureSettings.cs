using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nexus.Capture
{
    /// <summary>
    /// Preset configuration defining pixel dimensions for video capture.
    /// </summary>
    [Serializable]
    public struct ResolutionPreset
    {
        /// <summary>Preset display name.</summary>
        [SerializeField] public string name;

        /// <summary>Horizontal resolution in pixels.</summary>
        [SerializeField] public int width;

        /// <summary>Vertical resolution in pixels.</summary>
        [SerializeField] public int height;

        /// <summary>Initializes a new resolution preset.</summary>
        public ResolutionPreset(string name, int width, int height)
        {
            this.name = name;
            this.width = width;
            this.height = height;
        }
    }

    /// <summary>
    /// Configuration asset controlling shortcuts, playback speeds and capture targets.
    /// </summary>
    [CreateAssetMenu(menuName = "Nexus/Capture Settings")]
    public sealed class CaptureSettings : ScriptableObject
    {
        private static CaptureSettings s_cachedFallback;

        [SerializeField] private Key toggleKey = Key.F9;
        [SerializeField] private Key recordKey = Key.F10;
        [SerializeField] private Key hideUiKey = Key.H;
        [SerializeField] private Key slowerKey = Key.LeftBracket;
        [SerializeField] private Key fasterKey = Key.RightBracket;
        [SerializeField] private Key pauseKey = Key.P;
        [SerializeField] private Key stepKey = Key.O;
        [SerializeField] private Key freeCameraKey = Key.C;
        [SerializeField] private Key godModeKey = Key.G;
        [SerializeField] private float[] slowMotionSteps = { 1f, 0.5f, 0.25f, 0.1f };
        [SerializeField] private string outputFolder = "Recordings";
        [SerializeField] private List<ResolutionPreset> resolutionPresets = new List<ResolutionPreset>
        {
            new ResolutionPreset("Vertical", 1080, 1920),
            new ResolutionPreset("Horizontal", 1920, 1080)
        };
        [SerializeField] private int frameRate = 60;

        /// <summary>Keyboard shortcut to toggle Capture Mode open or closed.</summary>
        public Key ToggleKey { get => toggleKey; set => toggleKey = value; }

        /// <summary>Keyboard shortcut to request video recording toggle.</summary>
        public Key RecordKey { get => recordKey; set => recordKey = value; }

        /// <summary>Keyboard shortcut to toggle UI visibility.</summary>
        public Key HideUiKey { get => hideUiKey; set => hideUiKey = value; }

        /// <summary>Keyboard shortcut to step slower in slow-motion.</summary>
        public Key SlowerKey { get => slowerKey; set => slowerKey = value; }

        /// <summary>Keyboard shortcut to step faster in slow-motion.</summary>
        public Key FasterKey { get => fasterKey; set => fasterKey = value; }

        /// <summary>Keyboard shortcut to pause or resume time.</summary>
        public Key PauseKey { get => pauseKey; set => pauseKey = value; }

        /// <summary>Keyboard shortcut to advance one frame while paused.</summary>
        public Key StepKey { get => stepKey; set => stepKey = value; }

        /// <summary>Keyboard shortcut to request free camera toggle.</summary>
        public Key FreeCameraKey { get => freeCameraKey; set => freeCameraKey = value; }

        /// <summary>Keyboard shortcut to toggle god mode via registered hooks.</summary>
        public Key GodModeKey { get => godModeKey; set => godModeKey = value; }

        /// <summary>Array of playback speeds used by slow-motion controls.</summary>
        public float[] SlowMotionSteps { get => slowMotionSteps; set => slowMotionSteps = value; }

        /// <summary>Output directory where recordings are stored.</summary>
        public string OutputFolder { get => outputFolder; set => outputFolder = value; }

        /// <summary>List of supported resolution presets.</summary>
        public List<ResolutionPreset> ResolutionPresets { get => resolutionPresets; set => resolutionPresets = value; }

        /// <summary>Target capture frame rate.</summary>
        public int FrameRate { get => frameRate; set => frameRate = value; }

        /// <summary>
        /// Loads the active CaptureSettings asset or instantiates an in-memory fallback.
        /// </summary>
        public static CaptureSettings Load()
        {
            if (s_cachedFallback != null)
                return s_cachedFallback;

#if UNITY_EDITOR
            string[] guids = AssetDatabase.FindAssets("t:CaptureSettings");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var asset = AssetDatabase.LoadAssetAtPath<CaptureSettings>(path);
                if (asset != null)
                {
                    s_cachedFallback = asset;
                    return s_cachedFallback;
                }
            }
#endif

            s_cachedFallback = CreateInstance<CaptureSettings>();
            return s_cachedFallback;
        }

        internal static void ClearCache()
        {
            s_cachedFallback = null;
        }
    }
}
