using UnityEditor;

namespace Nexus.Capture.Editor
{
    /// <summary>
    /// Editor lifecycle watcher that restores modifications made by Capture Mode when exiting Play Mode.
    /// </summary>
    [InitializeOnLoad]
    public static class CapturePlayModeGuard
    {
        static CapturePlayModeGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                CaptureModeService.RestoreAll();
            }
        }
    }
}
