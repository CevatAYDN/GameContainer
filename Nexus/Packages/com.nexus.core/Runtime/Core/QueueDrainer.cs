using UnityEngine;

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
            if (ctx != null && _root.IsInitialized)
                ctx.HybridQueue.DrainThreadSafe();
        }

        private void LateUpdate()
        {
            var ctx = _root.Context;
            if (ctx != null && _root.IsInitialized)
                ctx.HybridQueue.DrainNextFrame();
        }
    }
}
