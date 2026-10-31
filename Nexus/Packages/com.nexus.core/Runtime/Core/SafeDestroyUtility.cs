using System.Threading;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Utility helper for safe object destruction that handles both EditMode unit tests
    /// and PlayMode runtime execution without throwing "Destroy may not be called from EditMode".
    /// A call from another thread (a dispose on a worker) is posted to the main thread instead of
    /// throwing UnityException.
    /// </summary>
    internal static class SafeDestroyUtility
    {
        private static int s_mainThreadId = -1;
        private static SynchronizationContext s_mainThreadContext;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CaptureMainThread()
        {
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
            s_mainThreadContext = SynchronizationContext.Current;
        }

        public static void SafeDestroy(UnityEngine.Object obj)
        {
            if (ReferenceEquals(obj, null)) return;
            if (PostedToMainThread(state => SafeDestroy((UnityEngine.Object)state), obj)) return;
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

        /// <summary>Destroys the component's GameObject; <c>gameObject</c> is read on the main thread too.</summary>
        public static void SafeDestroyGameObject(Component component)
        {
            if (ReferenceEquals(component, null)) return;
            if (PostedToMainThread(state => SafeDestroyGameObject((Component)state), component)) return;
            if (component == null) return;
            SafeDestroy(component.gameObject);
        }

        /// <summary>Posts the callback to the main thread and returns true when called from another thread.</summary>
        internal static bool PostedToMainThread(SendOrPostCallback callback, object state)
        {
            if (s_mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == s_mainThreadId) return false;
            // Unity objects, their == operator included, belong to the main thread.
            var context = s_mainThreadContext;
            if (context != null) context.Post(callback, state);
            else NexusRuntime.Logger?.LogError("[Nexus] SafeDestroy was called off the main thread and no main-thread context was captured; the object was not destroyed.");
            return true;
        }
    }
}
