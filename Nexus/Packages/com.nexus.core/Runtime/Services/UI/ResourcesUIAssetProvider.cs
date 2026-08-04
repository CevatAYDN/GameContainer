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

            // Complete the await the moment the load finishes instead of re-scheduling a
            // continuation every frame (the old while(!request.isDone) { await Task.Yield(); }
            // poll loop could delay the first window open by dozens of frames after Play).
            // AsyncOperation.completed (2020.1+) fires once when the load finishes, so this
            // bridge is non-polling and guaranteed to resume on the main thread.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            request.completed += _ => tcs.TrySetResult(true);
            await tcs.Task;

            var prefab = request.asset as GameObject;
            if (prefab == null)
            {
                // Synchronous fallback if the async request failed or returned null
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
                SafeDestroyUtility.SafeDestroy(windowInstance);
            }
        }
    }
}
