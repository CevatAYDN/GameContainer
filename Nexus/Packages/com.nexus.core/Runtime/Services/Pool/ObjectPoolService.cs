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
        void Prewarm(GameObject prefab, int count, Transform parent = null);
        /// <summary>
        /// Frame-budgeted prewarm. Spreads instantiation across multiple frames
        /// (at most <paramref name="instancesPerFrame"/> per frame) so a large pool no longer
        /// stalls the main thread in a single frame on level load. The returned task completes
        /// when all <paramref name="count"/> instances are pooled (or the context is disposed).
        /// </summary>
        System.Threading.Tasks.Task PrewarmAsync(GameObject prefab, int count, int instancesPerFrame = 8, Transform parent = null);
        GameObject Spawn(GameObject prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null);
        T Spawn<T>(T prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null) where T : Component;
        void Despawn(GameObject instance);
        void DespawnAfter(GameObject instance, float seconds);
        void ClearPool(GameObject prefab);
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

            public PoolData(GameObject prefab, Transform parent)
            {
                Prefab = prefab;
                var poolRoot = new GameObject($"Pool_{prefab.name}");
                poolRoot.transform.SetParent(parent);
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
            public int GetHashCode(UnityEngine.Object obj) =>
                obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
        private Transform _masterPoolRoot;
        private GameObject _masterRootObject;

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
            _masterRootObject = new GameObject("[Nexus_ObjectPool]");
            UnityEngine.Object.DontDestroyOnLoad(_masterRootObject);
            _masterPoolRoot = _masterRootObject.transform;
            return default;
        }

        public void Prewarm(GameObject prefab, int count, Transform parent = null)
        {
            if (prefab == null || count <= 0) return;
            var pool = GetOrCreatePool(prefab, parent);
            for (int i = 0; i < count; i++)
            {
                var instance = CreateInstance(pool);
                instance.SetActive(false);
                if (pool.Inactive.Count < MaxInactivePerPool)
                {
                    pool.Inactive.Push(instance);
                }
                else
                {
                    SafeDestroyUtility.SafeDestroy(instance);
                }
            }
        }

        // The synchronous Prewarm loop instantiated every instance in one
        // frame — an 800-object warm-up is a 50-200 ms main-thread spike exactly at level
        // transition, the worst possible moment. PrewarmAsync spreads the work across
        // frames via the same PoolTimerRunner coroutine host DespawnAfter already uses.
        public System.Threading.Tasks.Task PrewarmAsync(GameObject prefab, int count, int instancesPerFrame = 8, Transform parent = null)
        {
            if (prefab == null || count <= 0) return System.Threading.Tasks.Task.CompletedTask;
            if (instancesPerFrame <= 0) instancesPerFrame = 1;

            // No coroutine host (service disposed / not initialized): fall back to the
            // synchronous path so the pool is still warmed rather than silently skipped.
            if (_masterRootObject == null || !_masterRootObject.activeInHierarchy)
            {
                Prewarm(prefab, count, parent);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var pool = GetOrCreatePool(prefab, parent);
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
                    int budget = instancesPerFrame;
                    while (budget-- > 0 && created < count)
                    {
                        var instance = CreateInstance(pool);
                        instance.SetActive(false);
                        if (pool.Inactive.Count < MaxInactivePerPool)
                        {
                            pool.Inactive.Push(instance);
                        }
                        else
                        {
                            SafeDestroyUtility.SafeDestroy(instance);
                        }
                        created++;
                    }
                    yield return null; // next frame
                }
            }
            finally
            {
                tcs.TrySetResult(true);
                _pendingPrewarms.Remove(tcs);
            }
        }

        public GameObject Spawn(GameObject prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null)
        {
            if (prefab == null) return null;
            var pool = GetOrCreatePool(prefab, parent);
            GameObject instance;

            if (pool.Inactive.Count > 0)
            {
                instance = pool.Inactive.Pop();
            }
            else
            {
                instance = CreateInstance(pool);
            }

            var t = instance.transform;
            if (parent != null)
                t.SetParent(parent, false);

            // Quaternion.operator== is dot-product based, so `rotation == default` never
            // matches the all-zero default; compare components explicitly instead.
            bool isDefaultRotation = rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f;
            t.SetPositionAndRotation(position, isDefaultRotation ? Quaternion.identity : rotation);
            instance.SetActive(true);

            pool.Active.Add(instance);
            var spawnedKey = GetKey(instance);
            _poolsByInstanceId[spawnedKey] = pool;
            _spawnGenerations[spawnedKey] = ++_generationCounter;

            var poolables = _poolableCallbackDepth == 0 ? _poolableBuffer : new List<IPoolable>(8);
            poolables.Clear();
            instance.GetComponents(poolables);
            _poolableCallbackDepth++;
            try
            {
                for (int i = 0; i < poolables.Count; i++)
                {
                    try { poolables[i].OnSpawned(); }
                    catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                }
            }
            finally
            {
                _poolableCallbackDepth--;
                poolables.Clear();
            }

            return instance;
        }

        public T Spawn<T>(T prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null) where T : Component
        {
            if (prefab == null) return null;
            var instanceGo = Spawn(prefab.gameObject, position, rotation, parent);
            return instanceGo != null ? instanceGo.GetComponent<T>() : null;
        }

        public void Despawn(GameObject instance)
        {
            if (instance == null) return;
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
                _spawnGenerations.Remove(instanceKey);
                SafeDestroyUtility.SafeDestroy(instance);
                return;
            }

            if (!pool.Active.Remove(instance)) return;

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

            instance.SetActive(false);
            instance.transform.SetParent(pool.RootTransform, false);
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
                generation = -1L; // Unpooled GameObject fallback
            }

            if (_masterRootObject != null && _masterRootObject.activeInHierarchy)
            {
                var runner = _masterRootObject.GetComponent<PoolTimerRunner>() ?? _masterRootObject.AddComponent<PoolTimerRunner>();
                runner.StartCoroutine(DespawnCoroutine(instance, instanceKey, generation, seconds));
            }
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

        private PoolData GetOrCreatePool(GameObject prefab, Transform parent = null)
        {
            var prefabKey = GetKey(prefab);
            if (!_poolsByPrefabId.TryGetValue(prefabKey, out var pool))
            {
                pool = new PoolData(prefab, parent ?? _masterPoolRoot);
                _poolsByPrefabId[prefabKey] = pool;
            }
            return pool;
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

        private GameObject CreateInstance(PoolData pool)
        {
            var inst = UnityEngine.Object.Instantiate(pool.Prefab, pool.RootTransform);
            inst.name = pool.Prefab.name;
            return inst;
        }

        public void ClearPool(GameObject prefab)
        {
            if (prefab == null) return;
            var prefabKey = GetKey(prefab);
            if (_poolsByPrefabId.TryGetValue(prefabKey, out var pool))
            {
                while (pool.Inactive.Count > 0)
                {
                    var inst = pool.Inactive.Pop();
                    if (inst != null) SafeDestroyUtility.SafeDestroy(inst);
                }
                foreach (var active in pool.Active)
                {
                    if (active != null)
                    {
                        var activeKey = GetKey(active);
                        _poolsByInstanceId.Remove(activeKey);
                        _spawnGenerations.Remove(activeKey);
                        var poolables = active.GetComponents<IPoolable>();
                        for (int i = 0; i < poolables.Length; i++)
                        {
                            try { poolables[i].OnDespawned(); }
                            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                        }
                        SafeDestroyUtility.SafeDestroy(active);
                    }
                }
                pool.Active.Clear();
                if (pool.RootTransform != null) SafeDestroyUtility.SafeDestroy(pool.RootTransform.gameObject);
                _poolsByPrefabId.Remove(prefabKey);
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
            foreach (var kvp in _poolsByPrefabId)
            {
                var pool = kvp.Value;
                while (pool.Inactive.Count > 0)
                {
                    var inst = pool.Inactive.Pop();
                    if (inst != null) SafeDestroyUtility.SafeDestroy(inst);
                }
                foreach (var active in pool.Active)
                {
                    if (active != null)
                    {
                        // Per-instance dictionary cleanup now mirrors ClearPool
                        // exactly — previously the entries were only dropped by the bulk
                        // .Clear() below, so any per-instance teardown added between the loop
                        // and the final Clear would silently operate on stale registrations.
                        var activeKey = GetKey(active);
                        _poolsByInstanceId.Remove(activeKey);
                        _spawnGenerations.Remove(activeKey);
                        var poolables = active.GetComponents<IPoolable>();
                        for (int i = 0; i < poolables.Length; i++)
                        {
                            try { poolables[i].OnDespawned(); }
                            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                        }
                        SafeDestroyUtility.SafeDestroy(active);
                    }
                }
                if (pool.RootTransform != null) SafeDestroyUtility.SafeDestroy(pool.RootTransform.gameObject);
            }
            _poolsByPrefabId.Clear();
            _poolsByInstanceId.Clear();
            _spawnGenerations.Clear();
        }

        public override void Dispose()
        {
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

            ClearAllPools();
            if (_masterRootObject != null)
            {
                SafeDestroyUtility.SafeDestroy(_masterRootObject);
                _masterRootObject = null;
            }
        }

        private class PoolTimerRunner : MonoBehaviour { }
    }
}
