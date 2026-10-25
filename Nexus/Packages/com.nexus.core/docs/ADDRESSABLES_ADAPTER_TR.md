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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Nexus.Core;
using Nexus.Core.Services;

namespace YourGame.Infrastructure
{
    /// <summary>Addressables-backed IAssetLoadService. Handles are released via Release().</summary>
    public class AddressablesAssetLoadService : NexusService<IAssetLoadService>, IAssetLoadService
    {
        // Addressables anahtarı değil handle'ı bırakır: her başarılı yükleme anahtar başına tutulur, her Release(key) birini bırakır
        private readonly Dictionary<string, Stack<AsyncOperationHandle>> _handles = new Dictionary<string, Stack<AsyncOperationHandle>>();
        private readonly object _handlesLock = new object();

        public async Task<T> LoadAsync<T>(string key, CancellationToken ct = default) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Asset key must not be null or empty.", nameof(key));

            var handle = Addressables.LoadAssetAsync<T>(key);
            using (ct.Register(() => Addressables.Release(handle)))
            {
                await handle.Task.ConfigureAwait(false); // continuations don't touch Unity APIs
                if (handle.Status != AsyncOperationStatus.Succeeded)
                {
                    Addressables.Release(handle);
                    throw new InvalidOperationException($"Asset '{key}' failed to load: {handle.OperationException?.Message}");
                }
                Track(key, handle);
                return handle.Result;
            }
        }

        public T LoadSync<T>(string key) where T : UnityEngine.Object
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
            Track(key, handle);
            return handle.Result;
        }

        public void Release(string key)
        {
            AsyncOperationHandle handle;
            lock (_handlesLock)
            {
                // bu servis o anahtarla bir şey yüklemediyse bırakılacak bir şey yok
                if (key == null || !_handles.TryGetValue(key, out var loaded) || loaded.Count == 0) return;
                handle = loaded.Pop();
                if (loaded.Count == 0) _handles.Remove(key);
            }
            Addressables.Release(handle);
        }

        private void Track(string key, AsyncOperationHandle handle)
        {
            lock (_handlesLock)
            {
                if (!_handles.TryGetValue(key, out var loaded)) _handles[key] = loaded = new Stack<AsyncOperationHandle>();
                loaded.Push(handle);
            }
        }
    }
}
```

> **Not — Release tasarımı:** Seam anahtar-bazlıdır (`Release(string key)`) çünkü `Resources`'ta handle yoktur; Addressables ise handle bırakır (`Addressables.Release(key)` servisin yüklediği hiçbir şeyi bırakmaz). Bu yüzden adaptör yüklediği her handle'ı anahtar başına tutar ve her `Release(key)` birini bırakır; hiç yüklemediği bir anahtar yok sayılır. Kendi handle'ını tutan çağıran seam'i atlayıp `Addressables.Release(handle)` çağırabilir. İki servis arasında geçiş yaparken tek tutarlı kural: **bir aseti kim yüklediyse o bırakır.**

## 3. Doğrulama

- Adaptörü ekledikten sonra `ResourcesAssetLoadService`'i lifecycle'dan kaldırın ve projede `Resources.Load`/`Resources.LoadAsync` referansı kalmadığını doğrulayın (Addressables'a geçişin tamamlandığının kanıtı).
- Play mode'da bir ekran açıp kapatın; `Release` çağrılmadan yapılan her `LoadAsync` sonrası Memory Profiler'da handle sayısının sabit kaldığını kontrol edin (sızıntı testi).
- Editor testi: `IAssetLoadService.LoadAsync` → aset null değil; `Release` sonrası ikinci `LoadAsync` hâlâ çalışıyor (double-release yok).

## 4. Kapsam dışı

- Sahne yükleme Addressables üzerinden yapılıyorsa `ISceneLoader`'ın (SceneManagerExtensions.cs) `Addressables.LoadSceneAsync` tabanlı bir implementasyonuyla değiştirin — aynı seam deseni: arayüz pakette, adaptör projede.
