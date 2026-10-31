using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Whether this is a development build: the editor, or a player built with Development Build. The framework's
    /// development defaults and diagnostics read it at run time. Unity 6000.6 deprecates <c>DEVELOPMENT_BUILD</c> and
    /// removes it in 6.8, and its Managed Code Variant defaults to Release in development players too (#140).
    /// </summary>
    internal static class NexusBuild
    {
        // 0 unread, 1 release, 2 development. A player reads Debug.isDebugBuild once, on the main thread before any game
        // code (the primer below); every later read is this int. The editor is always a development build, as
        // Debug.isDebugBuild answers there, and needs no read: edit mode has no primer and a test may fire from a worker thread.
#if !UNITY_EDITOR
        private static int s_state;
#endif
        private static int s_override;

        internal static bool IsDevelopment
        {
            get
            {
                if (s_override != 0) return s_override == 2;
#if UNITY_EDITOR
                return true;
#else
                int state = s_state;
                if (state == 0)
                {
                    state = Debug.isDebugBuild ? 2 : 1;
                    s_state = state;
                }
                return state == 2;
#endif
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReadOnMainThread() => _ = IsDevelopment;

        /// <summary>Tests run the development or the release path; null returns to the build's own answer.</summary>
        internal static void OverrideForTests(bool? development) => s_override = development == null ? 0 : development.Value ? 2 : 1;
    }
}
