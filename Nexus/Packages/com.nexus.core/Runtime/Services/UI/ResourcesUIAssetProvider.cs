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
            var request = Resources.LoadAsync<GameObject>($"UI/Windows/{windowName}");
            while (!request.isDone)
            {
                await Task.Yield();
            }

            var prefab = request.asset as GameObject;
            if (prefab == null)
            {
                NexusRuntime.Logger?.LogError($"[ResourcesUIAssetProvider] Window prefab not found at path: UI/Windows/{windowName}");
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
