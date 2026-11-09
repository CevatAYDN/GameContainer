using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Lifecycle interface for pooled GameObjects/Components.
    /// </summary>
    [Preserve]
    public interface IPoolable
    {
        void OnSpawned();
        void OnDespawned();
    }

    public interface IObjectPoolService
    {
        /// <summary>
        /// Creates up to <paramref name="count"/> inactive instances. A pool keeps at most 128; a larger count is
        /// cut to what the pool can keep, with a warning. Activation callbacks wait until Spawn.
        /// </summary>
        void Prewarm(GameObject prefab, int count);
        /// <summary>
        /// Frame-budgeted prewarm. Spreads instantiation across multiple frames
        /// (at most <paramref name="instancesPerFrame"/> per frame) so a large pool no longer
        /// stalls the main thread in a single frame on level load. The returned task completes
        /// when the instances are pooled, is cancelled when the pool is cleared or the service is disposed, and fails when an
        /// instance cannot be created. The count is cut to what the pool can keep, as in <see cref="Prewarm"/>.
        /// </summary>
        System.Threading.Tasks.Task PrewarmAsync(GameObject prefab, int count, int instancesPerFrame = 8);
        /// <summary>
        /// Takes an instance from the prefab's pool, or creates one. <paramref name="parent"/> is the active
        /// instance's parent; without one the instance is a root of the active scene, as with Instantiate, and a scene
        /// load unloads it. Despawned instances wait under the service's own root, so a destroyed parent does not take
        /// the pool with it.
        /// A parenting/activation/spawn callback that clears, disposes or returns this acquisition
        /// interrupts it with InvalidOperationException (ObjectDisposedException on disposal).
        /// </summary>
        GameObject Spawn(GameObject prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null);
        T Spawn<T>(T prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null) where T : Component;
        void Despawn(GameObject instance);
        /// <summary>
        /// Despawns a live instance after <paramref name="seconds"/>, unless it was despawned (and maybe spawned
        /// again) meanwhile. An instance already back in its pool is refused with a warning. Without the
        /// initialized service there is no timer, so the instance is despawned at once, with a warning.
        /// </summary>
        void DespawnAfter(GameObject instance, float seconds);
        /// <summary>Clears the current pool. A callback may create a new pool whose leases remain owned.</summary>
        void ClearPool(GameObject prefab);
        /// <summary>Clears the pools present at entry; callback-created replacements remain owned.</summary>
        void ClearAllPools();
    }

    [Preserve]
    public class ObjectPoolService : NexusService<IObjectPoolService>, IObjectPoolService
    {
        // Hard cap on how many inactive instances a single prefab pool may retain.
        // Guards against unbounded memory growth under high allocation bursts (e.g. a
        // projectile barrage despawning thousands of objects that are never re-spawned).
        private const int MaxInactivePerPool = 128;

        private class PoolData
        {
            public GameObject Prefab { get; }
            public Stack<GameObject> Inactive { get; } = new();
            public HashSet<GameObject> Active { get; } = new();
            public Transform RootTransform { get; }
            public bool Clearing;

            public PoolData(GameObject prefab, Transform serviceRoot)
            {
                Prefab = prefab;
                var poolRoot = new GameObject($"Pool_{prefab.name}");
                poolRoot.SetActive(false);
                poolRoot.transform.SetParent(serviceRoot);
                RootTransform = poolRoot.transform;
            }
        }

        // Keyed by the managed object REFERENCE (identity), not by an int id: the old
        // int keys came from GetEntityId().GetHashCode(), whose hash collisions could map
        // two DIFFERENT objects onto one dictionary entry (a pool would then spawn from
        // the wrong prefab / treat one instance as another). A reference-comparer key is
        // collision-free (ReferenceEquals disambiguates), survives Unity's fake-null
        // destroyed objects (RuntimeHelpers.GetHashCode never touches native state), and
        // compiles in the harness stub environment, which stubs GetEntityId() as int.
        private readonly Dictionary<UnityEngine.Object, PoolData> _poolsByPrefabId = new(ObjectReferenceComparer.Instance);
        private readonly Dictionary<UnityEngine.Object, PoolData> _poolsByInstanceId = new(ObjectReferenceComparer.Instance);
        // Spawn-session generation per instance. Incremented on every Spawn so a pending
        // DespawnAfter timer can detect that the instance was despawned and RE-spawned while
        // it was waiting — despawning then would yank a live object out of the scene.
        private readonly Dictionary<UnityEngine.Object, long> _spawnGenerations = new(ObjectReferenceComparer.Instance);
        private long _generationCounter;

        private sealed class ObjectReferenceComparer : IEqualityComparer<UnityEngine.Object>
        {
            internal static readonly ObjectReferenceComparer Instance = new();
            public bool Equals(UnityEngine.Object x, UnityEngine.Object y) => ReferenceEquals(x, y);
            // ReferenceEquals, not ==: Unity's == calls a destroyed object null, which hashed it as 0 and lost its
            // entry (issue #184).
            public int GetHashCode(UnityEngine.Object obj) =>
                ReferenceEquals(obj, null) ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
        private Transform _masterPoolRoot;
        private GameObject _masterRootObject;
        private bool _disposed;

        private readonly List<IPoolable> _poolableBuffer = new(8);
        // Reentrancy depth for the shared buffer above: an OnSpawned/OnDespawned callback
        // may itself Spawn/Despawn another pooled object, which would clobber the buffer
        // mid-iteration. Nested calls rent a fresh local list instead.
        private int _poolableCallbackDepth;

        // Fix: PrewarmAsync TCSs pending completion. Completed (cancelled) in Dispose so a
        // caller awaiting a prewarm never hangs when the coroutine runner is torn down.
        private readonly HashSet<System.Threading.Tasks.TaskCompletionSource<bool>> _pendingPrewarms = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectPoolService));
            _masterRootObject = new GameObject("[Nexus_ObjectPool]");
            UnityEngine.Object.DontDestroyOnLoad(_masterRootObject);
            _masterPoolRoot = _masterRootObject.transform;
            return default;
        }

        public void Prewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0) return;
            var pool = GetOrCreatePool(prefab);
            AddInactive(pool, KeptCount(pool, count));
        }

        // The cap used to apply per instance: Prewarm(prefab, 500) created 500 and destroyed 372, silently (issue #184).
        private static int KeptCount(PoolData pool, int count)
        {
            int room = Math.Max(0, MaxInactivePerPool - pool.Inactive.Count);
            if (count > room)
                NexusLog.WarnAlways($"[Nexus] ObjectPoolService.Prewarm of '{pool.Prefab.name}' asked for {count} instances; a pool keeps {MaxInactivePerPool}, so {room} are created.");
            return Math.Min(count, room);
        }

        private void AddInactive(PoolData pool, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (pool.Clearing || _disposed) return;
                var instance = CreateInstance(pool);
                if (pool.Clearing || _disposed)
                {
                    SafeDestroyUtility.SafeDestroy(instance);
                    return;
                }
                instance.SetActive(false);
                if (DiscardInvalidLease(pool, instance)) return;
                if (pool.Inactive.Count < MaxInactivePerPool)
                {
                    pool.Inactive.Push(instance);
                }
                else
                {
                    SafeDestroyUtility.SafeDestroy(instance); // despawns filled the pool while an async prewarm ran
                }
            }
        }

        // The synchronous Prewarm loop instantiated every instance in one
        // frame — an 800-object warm-up is a 50-200 ms main-thread spike exactly at level
        // transition, the worst possible moment. PrewarmAsync spreads the work across
        // frames via the same PoolTimerRunner coroutine host DespawnAfter already uses.
        public System.Threading.Tasks.Task PrewarmAsync(GameObject prefab, int count, int instancesPerFrame = 8)
        {
            if (prefab == null || count <= 0) return System.Threading.Tasks.Task.CompletedTask;
            if (instancesPerFrame <= 0) instancesPerFrame = 1;

            // No coroutine host (service disposed / not initialized): fall back to the
            // synchronous path so the pool is still warmed rather than silently skipped.
            if (_masterRootObject == null || !_masterRootObject.activeInHierarchy)
            {
                Prewarm(prefab, count);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var pool = GetOrCreatePool(prefab);
            count = KeptCount(pool, count);
            if (count == 0) return System.Threading.Tasks.Task.CompletedTask;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPrewarms.Add(tcs);
            var runner = _masterRootObject.GetComponent<PoolTimerRunner>() ?? _masterRootObject.AddComponent<PoolTimerRunner>();
            runner.StartCoroutine(PrewarmCoroutine(pool, count, instancesPerFrame, tcs));
            return tcs.Task;
        }

        private IEnumerator PrewarmCoroutine(PoolData pool, int count, int instancesPerFrame, System.Threading.Tasks.TaskCompletionSource<bool> tcs)
        {
            // The finally block runs even when the runner is destroyed mid-coroutine
            // (Unity disposes the iterator), so the awaiting task always completes.
            try
            {
                int created = 0;
                while (created < count)
                {
                    int batch = Math.Min(instancesPerFrame, count - created);
                    if (!TryAddInactive(pool, batch, tcs)) yield break;
                    created += batch;
                    yield return null; // next frame
                }
            }
            finally
            {
                if (pool.Clearing || _disposed) tcs.TrySetCanceled();
                else tcs.TrySetResult(true); // a no-op once TryAddInactive failed the task
                _pendingPrewarms.Remove(tcs);
            }
        }

        // An iterator cannot catch around its yields. PrewarmAsync used to complete successfully after an instance
        // could not be created (issue #184).
        private bool TryAddInactive(PoolData pool, int count, System.Threading.Tasks.TaskCompletionSource<bool> tcs)
        {
            try
            {
                if (pool.Clearing || _disposed) { tcs.TrySetCanceled(); return false; }
                AddInactive(pool, count);
                if (pool.Clearing || _disposed) { tcs.TrySetCanceled(); return false; }
                return true;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogException(ex);
                tcs.TrySetException(ex);
                return false;
            }
        }

        public GameObject Spawn(GameObject prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null)
        {
            if (prefab == null) return null;
            var pool = GetOrCreatePool(prefab);
            GameObject instance = null;

            // An instance destroyed while it waited (by the game, or with a scene) is dropped, not handed out (issue #184).
            while (instance == null && pool.Inactive.Count > 0)
                instance = pool.Inactive.Pop();

            if (instance == null)
            {
                ForgetDestroyedActive(pool);
                instance = CreateInstance(pool);
            }
            RequireAcquisition(pool, instance);

            // Own the lease before parenting or activation can call back into Despawn (#360).
            pool.Active.Add(instance);
            var spawnedKey = GetKey(instance);
            _poolsByInstanceId[spawnedKey] = pool;
            long generation = ++_generationCounter;
            _spawnGenerations[spawnedKey] = generation;

            var t = instance.transform;
            if (parent != null)
            {
                t.SetParent(parent, false);
                RequireAcquisition(pool, instance, generation);
            }
            else
            {
                // As Instantiate without a parent: a root of the active scene, not a child of the pool root, which lives in
                // DontDestroyOnLoad and kept the instance through scene loads (issue #227).
                t.SetParent(null, false);
                RequireAcquisition(pool, instance, generation);
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.IsValid() && instance.scene != activeScene)
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance, activeScene);
            }

            // Quaternion.operator== is dot-product based, so `rotation == default` never
            // matches the all-zero default; compare components explicitly instead.
            bool isDefaultRotation = rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f;
            t.SetPositionAndRotation(position, isDefaultRotation ? Quaternion.identity : rotation);
            instance.SetActive(true);
            RequireAcquisition(pool, instance, generation);

            var poolables = _poolableCallbackDepth == 0 ? _poolableBuffer : new List<IPoolable>(8);
            poolables.Clear();
            instance.GetComponents(poolables);
            _poolableCallbackDepth++;
            try
            {
                for (int i = 0; i < poolables.Count; i++)
                {
                    RequireAcquisition(pool, instance, generation);
                    try { poolables[i].OnSpawned(); }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }
            finally
            {
                _poolableCallbackDepth--;
                poolables.Clear();
            }

            RequireAcquisition(pool, instance, generation);
            return instance;
        }

        private void RequireAcquisition(PoolData pool, GameObject instance, long generation)
        {
            RequireAcquisition(pool, instance);
            if (!pool.Active.Contains(instance) || !_spawnGenerations.TryGetValue(GetKey(instance), out long current) || current != generation)
                throw new InvalidOperationException("The pool acquisition was returned or replaced during a callback.");
        }

        private void RequireAcquisition(PoolData pool, GameObject instance)
        {
            if (!DiscardInvalidLease(pool, instance)) return;
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectPoolService));
            throw new InvalidOperationException("The pool acquisition was cleared or destroyed during a callback.");
        }

        private bool DiscardInvalidLease(PoolData pool, GameObject instance)
        {
            if (instance != null && !pool.Clearing && !_disposed) return false;
            if (!ReferenceEquals(instance, null)) Forget(instance);
            SafeDestroyUtility.SafeDestroy(instance);
            return true;
        }

        public T Spawn<T>(T prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null) where T : Component
        {
            if (prefab == null) return null;
            var instanceGo = Spawn(prefab.gameObject, position, rotation, parent);
            return instanceGo != null ? instanceGo.GetComponent<T>() : null;
        }

        public void Despawn(GameObject instance)
        {
            if (instance == null)
            {
                if (!ReferenceEquals(instance, null)) Forget(instance); // destroyed while it was active
                return;
            }
            var instanceKey = GetKey(instance);

            if (!_poolsByInstanceId.TryGetValue(instanceKey, out var pool))
            {
                // The "not in any pool" branch destroyed WHATEVER was passed —
                // including a prefab asset. Despawn(prefab) instead of Despawn(spawnedInstance)
                // silently destroyed the project's prefab reference (the damage only surfaced
                // on the next play). Registered prefabs are now explicitly protected.
                if (IsRegisteredPrefab(instance))
                {
                    NexusRuntime.Logger?.LogError(
                        $"[Nexus] ObjectPoolService.Despawn was called with the prefab asset '{instance.name}' itself, not a spawned instance. " +
                        "The prefab was NOT destroyed. Pass the instance returned by Spawn().");
                    return;
                }
                // A second Despawn of the same instance: it already waits in its pool, and destroying it
                // there handed the next Spawn a destroyed object.
                if (IsPooledInactive(instance))
                {
                    NexusRuntime.Logger?.LogWarning(
                        $"[Nexus] ObjectPoolService.Despawn was called twice for '{instance.name}'; it is already back in its pool.");
                    return;
                }
                _spawnGenerations.Remove(instanceKey);
                SafeDestroyUtility.SafeDestroy(instance);
                return;
            }

            if (pool.Clearing || !pool.Active.Remove(instance)) return;

            var poolables = _poolableCallbackDepth == 0 ? _poolableBuffer : new List<IPoolable>(8);
            poolables.Clear();
            instance.GetComponents(poolables);
            _poolableCallbackDepth++;
            try
            {
                for (int i = 0; i < poolables.Count; i++)
                {
                    try { poolables[i].OnDespawned(); }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }
            finally
            {
                _poolableCallbackDepth--;
                poolables.Clear();
            }

            // A callback may clear this pool or dispose the service while this lease is
            // outside Active. It must not be reparented to that now-destroyed root (#345).
            if (DiscardInvalidLease(pool, instance)) return;
            instance.SetActive(false);
            if (DiscardInvalidLease(pool, instance)) return;
            instance.transform.SetParent(pool.RootTransform, false);
            if (DiscardInvalidLease(pool, instance)) return;
            // Bound inactive retention per pool so a burst of Despawn calls cannot
            // grow the pool without limit. Overflow instances are destroyed — memory is
            // reclaimed instead of retained forever.
            if (pool.Inactive.Count < MaxInactivePerPool)
            {
                pool.Inactive.Push(instance);
            }
            else
            {
                SafeDestroyUtility.SafeDestroy(instance);
            }
            _poolsByInstanceId.Remove(instanceKey);
            _spawnGenerations.Remove(instanceKey);
        }

        public void DespawnAfter(GameObject instance, float seconds)
        {
            if (instance == null) return;
            var instanceKey = GetKey(instance);
            if (!_spawnGenerations.TryGetValue(instanceKey, out long generation))
            {
                // Taken for an unpooled object, the timer later despawned whatever live object reused it (issue #184).
                if (IsPooledInactive(instance))
                {
                    NexusRuntime.Logger?.LogWarning(
                        $"[Nexus] ObjectPoolService.DespawnAfter was called for '{instance.name}', which is already back in its pool; no timer was started.");
                    return;
                }
                generation = -1L; // Unpooled GameObject fallback
            }

            if (_masterRootObject == null || !_masterRootObject.activeInHierarchy)
            {
                NexusLog.WarnAlways($"[Nexus] ObjectPoolService.DespawnAfter for '{instance.name}' has no timer before InitializeAsync or after Dispose, so it is despawned now.");
                Despawn(instance);
                return;
            }

            var runner = _masterRootObject.GetComponent<PoolTimerRunner>() ?? _masterRootObject.AddComponent<PoolTimerRunner>();
            runner.StartCoroutine(DespawnCoroutine(instance, instanceKey, generation, seconds));
        }

        // Cache WaitForSeconds instances per unique delay value to avoid per-call heap allocation.
        // M3b fix: cap at 64 entries to prevent unbounded growth when callers pass non-repeating
        // float delays (e.g. computed values). The delay is rounded to the nearest 50 ms bucket
        // so similar values share a cached instance (0-alloc in the steady-state common case).
        private static readonly Dictionary<float, WaitForSeconds> s_waitForSecondsCache = new();
        private const int MaxWaitCacheEntries = 64;
        private const float WaitBucketMs = 0.05f; // 50 ms rounding bucket

        private IEnumerator DespawnCoroutine(GameObject instance, UnityEngine.Object instanceKey, long generation, float delay)
        {
            // Round to the nearest bucket so nearby delays share a cached WaitForSeconds.
            float key = (float)(Math.Round(delay / WaitBucketMs) * WaitBucketMs);
            if (!s_waitForSecondsCache.TryGetValue(key, out var wait))
            {
                // M3b: evict one entry if at cap before adding new entry.
                if (s_waitForSecondsCache.Count >= MaxWaitCacheEntries)
                {
                    // Remove the first (arbitrarily chosen) entry — keeps cache bounded.
                    var enumerator = s_waitForSecondsCache.GetEnumerator();
                    if (enumerator.MoveNext())
                        s_waitForSecondsCache.Remove(enumerator.Current.Key);
                    enumerator.Dispose();
                }
                wait = new WaitForSeconds(key);
                s_waitForSecondsCache[key] = wait;
            }
            yield return wait;
            // Only despawn if the instance is still in the SAME spawn session. If it was
            // manually despawned and re-spawned while the timer was pending, the generation
            // has advanced — despawning would kill the live re-spawned object.
            if ((_spawnGenerations.TryGetValue(instanceKey, out long current) && current == generation) || (generation == -1L && instance != null))
            {
                Despawn(instance);
            }
        }

        // The pool root is the service's own. It followed the first Spawn's parent, so its instances died with that
        // parent (issue #184).
        private PoolData GetOrCreatePool(GameObject prefab)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectPoolService));
            var prefabKey = GetKey(prefab);
            if (!_poolsByPrefabId.TryGetValue(prefabKey, out var pool) || pool.Clearing)
            {
                pool = new PoolData(prefab, _masterPoolRoot);
                _poolsByPrefabId[prefabKey] = pool;
            }
            return pool;
        }

        // Active instances destroyed with their parent stayed registered for good (issue #184). Swept when the pool
        // has to grow, so a spawn from the pool pays nothing.
        private void ForgetDestroyedActive(PoolData pool)
        {
            List<GameObject> destroyed = null;
            foreach (var active in pool.Active)
                if (active == null) (destroyed ??= new List<GameObject>()).Add(active);
            if (destroyed == null) return;
            foreach (var instance in destroyed) Forget(instance);
        }

        private void Forget(GameObject destroyed)
        {
            if (_poolsByInstanceId.TryGetValue(destroyed, out var pool)) pool.Active.Remove(destroyed);
            _poolsByInstanceId.Remove(destroyed);
            _spawnGenerations.Remove(destroyed);
        }

        /// <summary>
        /// True when <paramref name="obj"/> is one of the registered pool
        /// prefabs (reference compare, O(pools) — only reached on the invalid-call path).
        /// </summary>
        private bool IsRegisteredPrefab(GameObject obj)
        {
            foreach (var kvp in _poolsByPrefabId)
            {
                if (ReferenceEquals(kvp.Value.Prefab, obj))
                    return true;
            }
            return false;
        }

        /// <summary>True when <paramref name="obj"/> waits on a pool's inactive stack (O(pooled) — invalid-call path only).</summary>
        private bool IsPooledInactive(GameObject obj)
        {
            foreach (var kvp in _poolsByPrefabId)
            {
                foreach (var pooled in kvp.Value.Inactive)
                {
                    if (ReferenceEquals(pooled, obj))
                        return true;
                }
            }
            return false;
        }

        private GameObject CreateInstance(PoolData pool)
        {
            // The pool root is inactive, including while Instantiate runs Awake/OnEnable eligibility checks.
            var inst = UnityEngine.Object.Instantiate(pool.Prefab, pool.RootTransform);
            inst.name = pool.Prefab.name;
            inst.SetActive(false);
            return inst;
        }

        public void ClearPool(GameObject prefab)
        {
            if (prefab == null) return;
            var prefabKey = GetKey(prefab);
            if (_poolsByPrefabId.TryGetValue(prefabKey, out var pool))
                ClearPool(pool);
        }

        private void ClearPool(PoolData pool)
        {
            if (pool.Clearing) return;
            pool.Clearing = true;
            ClearMarkedPool(pool);
        }

        private void ClearMarkedPool(PoolData pool)
        {
            var activeSnapshot = new GameObject[pool.Active.Count];
            pool.Active.CopyTo(activeSnapshot);
            pool.Active.Clear();
            try
            {
                while (pool.Inactive.Count > 0)
                {
                    var inst = pool.Inactive.Pop();
                    if (inst != null) SafeDestroyUtility.SafeDestroy(inst);
                }
                foreach (var active in activeSnapshot)
                {
                    try
                    {
                        if (active == null) continue;
                        var poolables = active.GetComponents<IPoolable>();
                        for (int i = 0; i < poolables.Length; i++)
                        {
                            if (poolables[i] is UnityEngine.Object component && component == null) continue;
                            try { poolables[i].OnDespawned(); }
                            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                        }
                    }
                    finally
                    {
                        _poolsByInstanceId.Remove(GetKey(active));
                        _spawnGenerations.Remove(GetKey(active));
                        SafeDestroyUtility.SafeDestroy(active);
                    }
                }
            }
            finally
            {
                if (pool.RootTransform != null) SafeDestroyUtility.SafeDestroy(pool.RootTransform.gameObject);
                var key = GetKey(pool.Prefab);
                // A callback may have replaced this prefab's pool. Never remove the new
                // pool or its instance mappings while finishing the old one.
                if (_poolsByPrefabId.TryGetValue(key, out var current) && ReferenceEquals(current, pool))
                    _poolsByPrefabId.Remove(key);
            }
        }

        /// <summary>
        /// Identity key for the pool dictionaries. The managed object reference is the key
        /// (see the dictionary declarations): reference equality is collision-free where
        /// an int hash is not, works across Unity's fake-null destroyed objects, and does
        /// not depend on the unstable GetInstanceID/GetEntityId int conventions.
        /// </summary>
        private static UnityEngine.Object GetKey(UnityEngine.Object obj) => obj;

        public void ClearAllPools()
        {
            var snapshot = new PoolData[_poolsByPrefabId.Count];
            _poolsByPrefabId.Values.CopyTo(snapshot, 0);
            // Close every entry-snapshot pool before the first callback, so a spawn
            // into a pool cleared later also belongs to a replacement.
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Clearing) snapshot[i] = null;
                else snapshot[i].Clearing = true;
            }
            for (int i = 0; i < snapshot.Length; i++)
                if (snapshot[i] != null) ClearMarkedPool(snapshot[i]);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Complete pending PrewarmAsync tasks before the coroutine runner is destroyed
            // so awaiting callers never hang on a TCS that will no longer be signalled.
            if (_pendingPrewarms.Count > 0)
            {
                var pending = new System.Threading.Tasks.TaskCompletionSource<bool>[_pendingPrewarms.Count];
                _pendingPrewarms.CopyTo(pending);
                _pendingPrewarms.Clear();
                for (int i = 0; i < pending.Length; i++)
                {
                    pending[i].TrySetCanceled();
                }
            }

            try { ClearAllPools(); }
            finally
            {
                if (_masterRootObject != null) SafeDestroyUtility.SafeDestroy(_masterRootObject);
                _masterRootObject = null;
                _masterPoolRoot = null;
            }
        }

        private class PoolTimerRunner : MonoBehaviour { }
    }
}
