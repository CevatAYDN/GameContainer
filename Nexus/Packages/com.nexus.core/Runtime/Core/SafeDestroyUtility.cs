using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Utility helper for safe object destruction that handles both EditMode unit tests
    /// and PlayMode runtime execution without throwing "Destroy may not be called from EditMode".
    /// </summary>
    internal static class SafeDestroyUtility
    {
        public static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEngine.Object.DestroyImmediate(obj);
                return;
            }
#endif
            UnityEngine.Object.Destroy(obj);
        }
    }
}
