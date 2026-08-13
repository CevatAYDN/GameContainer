using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Default <see cref="IAssetLoadService"/> backed by the Resources API — no third-party
    /// dependency, works everywhere. See <c>docs/ADDRESSABLES_ADAPTER.md</c> to swap in an
    /// Addressables-backed implementation without touching callers.
    /// </summary>
    [Preserve]
    public class ResourcesAssetLoadService : NexusService<IAssetLoadService>, IAssetLoadService
    {
        public Task<T> LoadAsync<T>(string key, CancellationToken ct = default) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Asset key must not be null or empty.", nameof(key));

            var request = Resources.LoadAsync<T>(key);
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Propagate cancellation: the caller may abandon the load (scene teardown).
            // The request still completes in the background; the TCS stays canceled.
            ct.Register(() => tcs.TrySetCanceled(ct));

            // ResourceRequest.completed fires on the main thread; continuations are
            // scheduled via RunContinuationsAsynchronously so no sync-context capture
            // surprises (same discipline as SafeAsyncRunner).
            request.completed += op =>
            {
                var asset = (op as ResourceRequest)?.asset as T;
                if (asset == null)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"Asset '{key}' was not found in any Resources folder (or is not a {typeof(T).Name})."));
                    return;
                }
                tcs.TrySetResult(asset);
            };

            return tcs.Task;
        }

        public T LoadSync<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Asset key must not be null or empty.", nameof(key));

            var asset = Resources.Load<T>(key);
            if (asset == null)
                throw new InvalidOperationException(
                    $"Asset '{key}' was not found in any Resources folder (or is not a {typeof(T).Name}).");
            return asset;
        }

        // Resources has no per-key unload API — documented no-op so the seam contract is
        // explicit; the Addressables adapter implements real handle release here.
        public void Release(string key) { }
    }
}
