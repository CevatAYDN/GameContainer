using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Loads and releases the screens <see cref="UIManager"/> opens when no prefab is registered for them. The default is
    /// <see cref="ResourcesUIAssetProvider"/>; a game can back it with Addressables or asset bundles.
    /// </summary>
    public interface IUIAssetProvider
    {
        /// <summary>Loads the window named <paramref name="windowName"/> and instantiates it under <paramref name="parent"/>.</summary>
        Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent);

        /// <summary>
        /// Releases an instance <see cref="InstantiateWindowAsync"/> returned. UIManager hands each one back once, on the
        /// main thread: when it destroys the screen (a full pool, a failed open, its Dispose, a load that finished after
        /// its Dispose) and when it forgets a screen destroyed outside it. The instance can already be destroyed then:
        /// release what it holds (an Addressables handle) and destroy it only if it is alive.
        /// </summary>
        void ReleaseWindow(GameObject windowInstance);
    }
}
