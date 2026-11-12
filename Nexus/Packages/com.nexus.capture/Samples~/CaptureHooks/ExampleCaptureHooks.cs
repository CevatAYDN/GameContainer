using System.Collections.Generic;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nexus.Capture.Samples
{
    /// <summary>
    /// Reference implementation of <see cref="ICaptureHooks"/> demonstrating game-specific hooks for Capture Mode.
    /// </summary>
    public sealed class ExampleCaptureHooks : ICaptureHooks
    {
        private static readonly string[] s_presets = { "Restart level" };

        /// <summary>
        /// Gets whether god mode is currently active.
        /// </summary>
        // A real game sets its own runtime flag instead (e.g. PlayerHealth.Invulnerable = on;).
        public static bool GodMode { get; private set; }

        /// <summary>
        /// Gets the display name of the game for capture metadata.
        /// </summary>
        public string GameName => Application.productName;

        /// <summary>
        /// Enables or disables god mode or invulnerability in the game.
        /// </summary>
        /// <param name="on">True to enable god mode; false to disable.</param>
        public void SetGodMode(bool on)
        {
            GodMode = on;
        }

        /// <summary>
        /// Gets the total number of playable levels configured in build settings.
        /// </summary>
        public int LevelCount => SceneManager.sceneCountInBuildSettings;

        /// <summary>
        /// Jumps directly to the specified level index in build settings.
        /// </summary>
        /// <param name="index">Zero-based level index.</param>
        public void JumpToLevel(int index)
        {
            if (index >= 0 && index < SceneManager.sceneCountInBuildSettings)
            {
                SceneManager.LoadScene(index);
            }
            else
            {
                NexusLog.WarnAlways($"[Nexus Capture] Level index {index} is out of range (0..{SceneManager.sceneCountInBuildSettings - 1}).");
            }
        }

        /// <summary>
        /// Gets the list of available scenario or state presets.
        /// </summary>
        public IReadOnlyList<string> Presets => s_presets;

        /// <summary>
        /// Applies the scenario preset at the specified index.
        /// </summary>
        /// <param name="index">Zero-based preset index.</param>
        public void ApplyPreset(int index)
        {
            if (index == 0)
            {
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            }
            else
            {
                NexusLog.WarnAlways($"[Nexus Capture] Preset index {index} is out of range.");
            }
        }

        // Runs after every domain reload, so the hooks survive entering Play Mode with or without domain reload.
        [UnityEditor.InitializeOnLoadMethod]
        private static void Register() => CaptureModeService.RegisterHooks(new ExampleCaptureHooks());
    }
}
