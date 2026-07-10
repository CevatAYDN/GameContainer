using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// UI Penceresi Prefab'lerinin yüklenmesini ve serbest bırakılmasını soyutlayan arayüz.
    /// Farklı oyunlarda Addressables, AssetBundles veya Resources sistemleri ile genişletilebilir.
    /// </summary>
    public interface IUIAssetProvider
    {
        /// <summary>
        /// Belirtilen pencereyi asenkron yükler ve belirtilen parent altına instantiate eder.
        /// </summary>
        Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent);

        /// <summary>
        /// Instantiate edilmiş pencere örneğini serbest bırakır/yok eder.
        /// </summary>
        void ReleaseWindow(GameObject windowInstance);
    }
}
