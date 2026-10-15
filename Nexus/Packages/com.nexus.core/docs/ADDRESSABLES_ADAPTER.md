# Addressables Adapter — `IAssetLoadService`

**Neden bu dosya:** `com.nexus.core` bilinçli olarak üçüncü taraf bağımlılıksız kalıyor (sıfır-GC + AOT + paket hafifliği). Adreslenebilir aset hattı isteyen stüdyolar için seam (`IAssetLoadService`) pakette, adaptör **bu dokümanda** — Addressables paketini kurduğunuzda kendi projenize yapıştırın. Çağıran kod hiç değişmez.

## 1. Kurulum

1. Package Manager → Addressables'i yükleyin (Unity 6'da `com.unity.addressables`).
2. Aşağıdaki dosyayı projenize ekleyin: `Assets/Scripts/Infrastructure/AddressablesAssetLoadService.cs`.
3. Lifecycle'a bağlayın:

```csharp
builder.BindService<IAssetLoadService, AddressablesAssetLoadService>();
```

Varsayılan `ResourcesAssetLoadService` yerine geçer; `LoadAsync`/`LoadSync`/`Release` çağıran tüm kod (UI, scene loader, sabit içerik) dokunulmadan çalışır.

## 2. Adaptör

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Nexus.Core.Services;

namespace YourGame.Infrastructure
{
    /// <summary>Addressables-backed IAssetLoadService. Handles are released via Release().</summary>
    public class AddressablesAssetLoadService : NexusService<IAssetLoadService>, IAssetLoadService
    {
        public async Task<T> LoadAsync<T>(string key, CancellationToken ct = default) where T : Object
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Asset key must not be null or empty.", nameof(key));

            var handle = Addressables.LoadAssetAsync<T>(key);
            using (ct.Register(() => Addressables.Release(handle)))
            {
                await handle.Task.ConfigureAwait(false); // continuations don't touch Unity APIs
                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw new InvalidOperationException($"Asset '{key}' failed to load: {handle.OperationException?.Message}");
                return handle.Result;
            }
        }

        public T LoadSync<T>(string key) where T : Object
        {
            // Addressables has no true sync API; the nearest is WaitForCompletion (blocks the
            // main thread until the async op finishes — fine for startup/bootstrapping).
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Asset key must not be null or empty.", nameof(key));

            var handle = Addressables.LoadAssetAsync<T>(key);
            handle.WaitForCompletion();
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Addressables.Release(handle);
                throw new InvalidOperationException($"Asset '{key}' failed to load: {handle.OperationException?.Message}");
            }
            return handle.Result;
        }

        public void Release(string key)
        {
            // Track key → handle in a Dictionary<string, AsyncOperationHandle> when the
            // seam needs per-key release; for handle-first callers prefer the overload below.
            Addressables.Release(key);
        }
    }
}
```

> **Not — Release tasarımı:** Seam anahtar-bazlıdır (`Release(string key)`) çünkü `Resources`'ta handle yoktur. Addressables'ta handle-first release daha doğaldır; eğer asıl akışınız handle tutuyorsa ya yukarıdaki `Dictionary<string, AsyncOperationHandle>` eşlemesini tutun ya da `LoadAsync` sonrası handle'ı çağıranın saklayıp `Addressables.Release(handle)` çağırmasına izin verin (seam'i yalnızca Resources varsayılanı için kullanın). İki servis arasında geçiş yaparken tek tutarlı kural: **bir aseti kim yüklediyse o bırakır.**

## 3. Doğrulama

- Adaptörü ekledikten sonra `ResourcesAssetLoadService`'i lifecycle'dan kaldırın ve projede `Resources.Load`/`Resources.LoadAsync` referansı kalmadığını doğrulayın (Addressables'a geçişin tamamlandığının kanıtı).
- Play mode'da bir ekran açıp kapatın; `Release` çağrılmadan yapılan her `LoadAsync` sonrası Memory Profiler'da handle sayısının sabit kaldığını kontrol edin (sızıntı testi).
- Editor testi: `IAssetLoadService.LoadAsync` → aset null değil; `Release` sonrası ikinci `LoadAsync` hâlâ çalışıyor (double-release yok).

## 4. Kapsam dışı

- Sahne yükleme Addressables üzerinden yapılıyorsa `ISceneLoader`'ın (SceneManagerExtensions.cs) `Addressables.LoadSceneAsync` tabanlı bir implementasyonuyla değiştirin — aynı seam deseni: arayüz pakette, adaptör projede.
