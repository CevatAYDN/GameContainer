#pragma warning disable 0619

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
#pragma warning disable CS0619
#pragma warning disable 0619
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

        private readonly Dictionary<int, PoolData> _poolsByPrefabId = new();
        private readonly Dictionary<int, PoolData> _poolsByInstanceId = new();
        // Spawn-session generation per instance id. Incremented on every Spawn so a pending
        // DespawnAfter timer can detect that the instance was despawned and RE-spawned while
        // it was waiting — despawning then would yank a live object out of the scene.
        private readonly Dictionary<int, long> _spawnGenerations = new();
        private long _generationCounter;
        private Transform _masterPoolRoot;
        private GameObject _masterRootObject;

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
                pool.Inactive.Push(instance);
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

            t.SetPositionAndRotation(position, rotation == default ? Quaternion.identity : rotation);
            instance.SetActive(true);

            pool.Active.Add(instance);
            int spawnedId = GetId(instance);
            _poolsByInstanceId[spawnedId] = pool;
            _spawnGenerations[spawnedId] = ++_generationCounter;

            var poolables = instance.GetComponents<IPoolable>();
            for (int i = 0; i < poolables.Length; i++)
            {
                poolables[i].OnSpawned();
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
            int instanceId = GetId(instance);

            if (!_poolsByInstanceId.TryGetValue(instanceId, out var pool))
            {
                _spawnGenerations.Remove(instanceId);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            if (!pool.Active.Remove(instance)) return;

            var poolables = instance.GetComponents<IPoolable>();
            for (int i = 0; i < poolables.Length; i++)
            {
                try { poolables[i].OnDespawned(); }
                catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
            }

            instance.SetActive(false);
            instance.transform.SetParent(pool.RootTransform, false);
            pool.Inactive.Push(instance);
            _poolsByInstanceId.Remove(instanceId);
            _spawnGenerations.Remove(instanceId);
        }

        public void DespawnAfter(GameObject instance, float seconds)
        {
            if (instance == null) return;
            int instanceId = GetId(instance);
            if (!_spawnGenerations.TryGetValue(instanceId, out long generation)) return;

            if (_masterRootObject != null && _masterRootObject.activeInHierarchy)
            {
                var runner = _masterRootObject.GetComponent<PoolTimerRunner>() ?? _masterRootObject.AddComponent<PoolTimerRunner>();
                runner.StartCoroutine(DespawnCoroutine(instance, instanceId, generation, seconds));
            }
        }

        private IEnumerator DespawnCoroutine(GameObject instance, int instanceId, long generation, float delay)
        {
            yield return new WaitForSeconds(delay);
            // Only despawn if the instance is still in the SAME spawn session. If it was
            // manually despawned and re-spawned while the timer was pending, the generation
            // has advanced — despawning would kill the live re-spawned object.
            if (_spawnGenerations.TryGetValue(instanceId, out long current) && current == generation)
            {
                Despawn(instance);
            }
        }

        private PoolData GetOrCreatePool(GameObject prefab, Transform parent = null)
        {
            int prefabId = GetId(prefab);
            if (!_poolsByPrefabId.TryGetValue(prefabId, out var pool))
            {
                pool = new PoolData(prefab, parent ?? _masterPoolRoot);
                _poolsByPrefabId[prefabId] = pool;
            }
            return pool;
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
            int prefabId = GetId(prefab);
            if (_poolsByPrefabId.TryGetValue(prefabId, out var pool))
            {
                while (pool.Inactive.Count > 0)
                {
                    var inst = pool.Inactive.Pop();
                    if (inst != null) UnityEngine.Object.Destroy(inst);
                }
                foreach (var active in pool.Active)
                {
                    if (active != null)
                    {
                        int activeId = GetId(active);
                        _poolsByInstanceId.Remove(activeId);
                        _spawnGenerations.Remove(activeId);
                        var poolables = active.GetComponents<IPoolable>();
                        for (int i = 0; i < poolables.Length; i++)
                        {
                            try { poolables[i].OnDespawned(); }
                            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                        }
                        UnityEngine.Object.Destroy(active);
                    }
                }
                pool.Active.Clear();
                if (pool.RootTransform != null) UnityEngine.Object.Destroy(pool.RootTransform.gameObject);
                _poolsByPrefabId.Remove(prefabId);
            }
        }

        private static readonly Func<UnityEngine.Object, int> s_getIdDelegate = CreateGetIdDelegate();

        private static Func<UnityEngine.Object, int> CreateGetIdDelegate()
        {
            var type = typeof(UnityEngine.Object);
            var method = type.GetMethod("GetEntityId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                      ?? type.GetMethod("GetInstanceID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (method != null)
            {
                try
                {
                    return (Func<UnityEngine.Object, int>)Delegate.CreateDelegate(typeof(Func<UnityEngine.Object, int>), null, method);
                }
                catch
                {
                    // Fallback: GetInstanceID may fail on AOT/IL2CPP platforms where
                    // Delegate.CreateDelegate for non-static generic methods is restricted.
                }
            }
            return obj => obj.GetHashCode();
        }

        private static int GetId(UnityEngine.Object obj)
        {
            if (obj == null) return 0;
            return s_getIdDelegate(obj);
        }

        public void ClearAllPools()
        {
            foreach (var kvp in _poolsByPrefabId)
            {
                var pool = kvp.Value;
                while (pool.Inactive.Count > 0)
                {
                    var inst = pool.Inactive.Pop();
                    if (inst != null) UnityEngine.Object.Destroy(inst);
                }
                foreach (var active in pool.Active)
                {
                    if (active != null)
                    {
                        var poolables = active.GetComponents<IPoolable>();
                        for (int i = 0; i < poolables.Length; i++)
                        {
                            try { poolables[i].OnDespawned(); }
                            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
                        }
                        UnityEngine.Object.Destroy(active);
                    }
                }
                if (pool.RootTransform != null) UnityEngine.Object.Destroy(pool.RootTransform.gameObject);
            }
            _poolsByPrefabId.Clear();
            _poolsByInstanceId.Clear();
            _spawnGenerations.Clear();
        }

        public override void Dispose()
        {
            ClearAllPools();
            if (_masterRootObject != null)
            {
                UnityEngine.Object.Destroy(_masterRootObject);
                _masterRootObject = null;
            }
        }

        private class PoolTimerRunner : MonoBehaviour { }
    }
}
