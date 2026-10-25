# Addressables Adapter — `IAssetLoadService`

**Why this file:** `com.nexus.core` deliberately stays free of third-party dependencies (zero GC + AOT + a light package). For studios that want an Addressables asset pipeline, the seam (`IAssetLoadService`) is in the package and the adapter is **in this document** — paste it into your own project once you have installed the Addressables package. Calling code does not change at all.

## 1. Setup

1. Package Manager → install Addressables (`com.unity.addressables` in Unity 6).
2. Add the file below to your project: `Assets/Scripts/Infrastructure/AddressablesAssetLoadService.cs`.
3. Bind it in your lifecycle:

```csharp
builder.BindService<IAssetLoadService, AddressablesAssetLoadService>();
```

It replaces the default `ResourcesAssetLoadService`; all code that calls `LoadAsync`/`LoadSync`/`Release` (UI, scene loader, static content) keeps working untouched.

## 2. Adapter

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
        // Addressables releases handles, not keys: every successful load is kept per key, one Release(key) releases one
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
                // nothing this service loaded under that key: nothing to release
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

> **Note — Release design:** The seam is key-based (`Release(string key)`) because `Resources` has no handles, while Addressables releases handles (`Addressables.Release(key)` would release nothing the service loaded). The adapter therefore keeps every handle it loaded per key and releases one per `Release(key)`; a key it never loaded is ignored. A caller that keeps its own handle can call `Addressables.Release(handle)` instead and skip the seam. When switching between the two services there is one consistent rule: **whoever loaded an asset releases it.**

## 3. Verification

- After adding the adapter, remove `ResourcesAssetLoadService` from the lifecycle and verify that no `Resources.Load`/`Resources.LoadAsync` references remain in the project (proof that the move to Addressables is complete).
- In Play mode, open and close a screen; after each `LoadAsync` made without a `Release` call, check in the Memory Profiler that the handle count stays constant (leak test).
- Editor test: `IAssetLoadService.LoadAsync` → the asset is not null; after `Release`, a second `LoadAsync` still works (no double release).

## 4. Out of scope

- If scenes are loaded through Addressables, replace `ISceneLoader` (SceneManagerExtensions.cs) with an implementation based on `Addressables.LoadSceneAsync` — the same seam pattern: interface in the package, adapter in the project.
