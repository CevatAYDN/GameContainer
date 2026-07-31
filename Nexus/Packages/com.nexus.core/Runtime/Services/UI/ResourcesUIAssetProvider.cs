using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Varsayılan Resources.LoadAsync tabanlı IUIAssetProvider implementasyonu.
    /// </summary>
    public class ResourcesUIAssetProvider : IUIAssetProvider
    {
        public async Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent)
        {
            string path = $"UI/Windows/{windowName}";
            var request = Resources.LoadAsync<GameObject>(path);
            int timeout = 300; // 300 frames bound to prevent permanent deadlock
            while (!request.isDone && timeout > 0)
            {
                await Task.Yield();
                timeout--;
            }

            var prefab = request.asset as GameObject;
            if (prefab == null)
            {
                // Synchronous fallback if async request timed out or returned null
                prefab = Resources.Load<GameObject>(path);
            }

            if (prefab == null)
            {
                NexusRuntime.Logger?.LogError($"[ResourcesUIAssetProvider] Window prefab not found at path: {path}");
                return null;
            }

            var inst = UnityEngine.Object.Instantiate(prefab, parent);
            inst.name = windowName;
            return inst;
        }

        public void ReleaseWindow(GameObject windowInstance)
        {
            if (windowInstance != null)
            {
                UnityEngine.Object.Destroy(windowInstance);
            }
        }
    }
}
