using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Drains the Nexus hybrid queue from the Unity main-thread lifecycle.
    /// Extracted from Root so each MonoBehaviour owns one concern.
    /// </summary>
    [DefaultExecutionOrder(-900)] // After Root (-1000), before most scripts
    [Preserve]
    public class QueueDrainer : MonoBehaviour
    {
        private Root _root;

        private void Awake()
        {
            _root = GetComponent<Root>();
            if (_root == null)
            {
                Debug.LogError("[Nexus] QueueDrainer requires a Root component on the same GameObject.");
                enabled = false;
            }
        }

        private void Update()
        {
            var ctx = _root.Context;
            // Use null-conditional in case HybridQueue is null after dispose.
            if (ctx != null && _root.IsInitialized)
                ctx.HybridQueue?.DrainThreadSafe();
        }

        private void LateUpdate()
        {
            var ctx = _root.Context;
            // Use null-conditional in case HybridQueue is null after dispose.
            if (ctx != null && _root.IsInitialized)
                ctx.HybridQueue?.DrainNextFrame();
        }
    }

    /// <summary>
    /// Drains the queues of every initialized context that no Root drains: one made by <c>ContextFactory.StartAsync</c>,
    /// <c>NexusRuntime.CreatePureContextAsync</c> or <c>new Context()</c> and initialized. Before, only a Root's
    /// <see cref="QueueDrainer"/> drained, so <c>FireThreadSafe</c> and <c>FireNextFrame</c> on such a context were accepted
    /// and never ran, with no line, while its queue grew (issue #169). Created before the first scene loads in a player and
    /// in play mode; edit mode has no frame loop, so there a context without a Root is drained by whoever drives it.
    /// </summary>
    [DefaultExecutionOrder(-900)] // the same order as QueueDrainer
    [AddComponentMenu("")]
    [Preserve]
    internal sealed class ContextQueueDrainer : MonoBehaviour
    {
        private static ContextQueueDrainer s_instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Create()
        {
            if (s_instance != null) return;   // Unity's null: a drainer destroyed with the last play session is made again
            var drainerObject = new GameObject("[Nexus_ContextQueueDrainer]");
            DontDestroyOnLoad(drainerObject);
            s_instance = drainerObject.AddComponent<ContextQueueDrainer>();
        }

        private void Update() => DrainContexts(threadSafe: true);
        private void LateUpdate() => DrainContexts(threadSafe: false);

        private static void DrainContexts(bool threadSafe)
        {
            var contexts = NexusRuntime.ActiveContexts;
            for (int i = 0; i < contexts.Count; i++)
            {
                // a Root's context is drained by its QueueDrainer; a context still starting is drained once it is initialized, as a Root's
                if (!(contexts[i] is Context context) || context.DrainedByRoot || !context.IsInitialized) continue;
                if (threadSafe) context.HybridQueue?.DrainThreadSafe();
                else context.HybridQueue?.DrainNextFrame();
            }
        }
    }
}
