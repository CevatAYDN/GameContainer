using System;
using Nexus.Core.Services;
using UnityEngine;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class ObjectPoolReentrancyTests
    {
        public sealed class Callback : MonoBehaviour, IPoolable
        {
            public Action OnEnd;
            public Action OnBegin;
            public int Exits;
            public void OnSpawned() => OnBegin?.Invoke();
            public void OnDespawned() { Exits++; OnEnd?.Invoke(); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static GameObject FindPoolRoot(GameObject prefab)
        {
            string rootName = "Pool_" + prefab.name;
            foreach (var root in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                if (root.name == rootName) return root;
            return null;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ClearPool_ReentrantSpawn_PreservesReplacementOwnership() => ClearWithSpawn(false);

#if UNITY_EDITOR
        [Test]
#endif
        public void ClearAll_ReentrantSpawn_PreservesReplacementOwnership() => ClearWithSpawn(true);

        private static void ClearWithSpawn(bool all)
        {
            var prefab = new GameObject("PoolReentry_" + Guid.NewGuid().ToString("N"));
            var pool = new ObjectPoolService();
            Callback callback = null;
            try
            {
                var original = pool.Spawn(prefab);
                var oldRoot = FindPoolRoot(prefab);
                Require(oldRoot != null, "the pool root must exist before clearing its leases");
                callback = original.AddComponent<Callback>();
                GameObject replacement = null;
                callback.OnEnd = () =>
                {
                    callback.OnEnd = null;
                    replacement = pool.Spawn(prefab);
                };
                if (all) pool.ClearAllPools();
                else pool.ClearPool(prefab);
                Require(!original && !oldRoot && callback.Exits == 1,
                    "the original lease and root must be destroyed, with one callback");
                Require(replacement != null && replacement.activeSelf, "the callback's new lease must be valid");
                pool.Despawn(replacement);
                Require(replacement != null && !replacement.activeSelf, "the replacement must still belong to its pool");
                Require(ReferenceEquals(replacement, pool.Spawn(prefab)), "the replacement must be reusable");
            }
            finally
            {
                if (!ReferenceEquals(callback, null)) callback.OnEnd = null;
                pool.Dispose();
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ClearAll_ReentrantDespawnAndClear_ExitsEachLeaseOnce()
        {
            var prefab = new GameObject("PoolNestedClear_" + Guid.NewGuid().ToString("N"));
            using var pool = new ObjectPoolService();
            try
            {
                var first = pool.Spawn(prefab);
                var second = pool.Spawn(prefab);
                var firstCallback = first.AddComponent<Callback>();
                var secondCallback = second.AddComponent<Callback>();
                bool entered = false;
                firstCallback.OnEnd = () =>
                {
                    if (entered) return; // bound the red-code probe; Exits must still equal one
                    entered = true;
                    pool.Despawn(second);
                    pool.ClearPool(prefab);
                    pool.ClearAllPools();
                };
                pool.ClearAllPools();
                Require(!first && !second && firstCallback.Exits == 1 && secondCallback.Exits == 1,
                    "nested cleanup must destroy both leases and notify each exactly once");
            }
            finally { UnityEngine.Object.DestroyImmediate(prefab); }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Despawn_ReentrantClearOrDispose_DoesNotReturnToDestroyedRoot()
        {
            foreach (bool dispose in new[] { false, true })
            {
                var prefab = new GameObject("PoolDespawnClear_" + Guid.NewGuid().ToString("N"));
                using var pool = new ObjectPoolService();
                try
                {
                    var lease = pool.Spawn(prefab);
                    var root = FindPoolRoot(prefab);
                    Require(root != null, "the pool root must exist before the reentrant despawn");
                    var callback = lease.AddComponent<Callback>();
                    callback.OnEnd = () =>
                    {
                        if (dispose) pool.Dispose();
                        else pool.ClearPool(prefab);
                    };
                    pool.Despawn(lease);
                    Require(!lease && !root && callback.Exits == 1,
                        "the in-flight despawn must destroy its lease instead of returning to a cleared pool");
                    if (!dispose) Require(pool.Spawn(prefab), "a cleared service must still create a fresh pool");
                }
                finally { pool.Dispose(); UnityEngine.Object.DestroyImmediate(prefab); }
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Dispose_RejectsCallbackAcquisition_AndCompletesCleanup()
        {
            var prefab = new GameObject("PoolDispose_" + Guid.NewGuid().ToString("N"));
            using var pool = new ObjectPoolService();
            try
            {
                var lease = pool.Spawn(prefab);
                var root = FindPoolRoot(prefab);
                Require(root != null, "the pool root must exist before disposal");
                var callback = lease.AddComponent<Callback>();
                bool rejected = false;
                bool entered = false;
                callback.OnEnd = () =>
                {
                    if (entered) return; // keep the pre-fix recursive Dispose from crashing the test process
                    entered = true;
                    try { pool.Spawn(prefab); }
                    catch (ObjectDisposedException) { rejected = true; }
                    pool.Dispose();
                };
                pool.Dispose();
                Require(rejected && !lease && !root && callback.Exits == 1,
                    "disposal must close admission before callbacks and still destroy the lease/root");
            }
            finally { UnityEngine.Object.DestroyImmediate(prefab); }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ClearAll_CallbackCreatesAnotherPrefabPool_LeavesItOwned()
        {
            var firstPrefab = new GameObject("PoolOld_" + Guid.NewGuid().ToString("N"));
            var newPrefab = new GameObject("PoolNew_" + Guid.NewGuid().ToString("N"));
            using var pool = new ObjectPoolService();
            try
            {
                var original = pool.Spawn(firstPrefab);
                var callback = original.AddComponent<Callback>();
                GameObject replacement = null;
                callback.OnEnd = () => replacement = pool.Spawn(newPrefab);
                pool.ClearAllPools();
                Require(!original && replacement, "adding a new prefab pool must not interrupt the clear snapshot");
                pool.Despawn(replacement);
                Require(replacement && !replacement.activeSelf, "the new prefab lease must not lose ownership");
                Require(ReferenceEquals(replacement, pool.Spawn(newPrefab)), "the callback-created pool must remain reusable");
            }
            finally
            {
                pool.Dispose();
                UnityEngine.Object.DestroyImmediate(firstPrefab);
                UnityEngine.Object.DestroyImmediate(newPrefab);
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ClearAll_CrossPoolCallbacks_PreserveBothNewLeasesRegardlessOfIterationOrder()
        {
            var firstPrefab = new GameObject("PoolCrossA_" + Guid.NewGuid().ToString("N"));
            var secondPrefab = new GameObject("PoolCrossB_" + Guid.NewGuid().ToString("N"));
            using var pool = new ObjectPoolService();
            try
            {
                var first = pool.Spawn(firstPrefab);
                var second = pool.Spawn(secondPrefab);
                GameObject replacementA = null, replacementB = null;
                first.AddComponent<Callback>().OnEnd = () => replacementB = pool.Spawn(secondPrefab);
                second.AddComponent<Callback>().OnEnd = () => replacementA = pool.Spawn(firstPrefab);
                pool.ClearAllPools();
                Require(!first && !second && replacementA && replacementB,
                    "both callback-created leases must survive the entry snapshot, including the pool cleared last");
                pool.Despawn(replacementA);
                pool.Despawn(replacementB);
                Require(ReferenceEquals(replacementA, pool.Spawn(firstPrefab))
                    && ReferenceEquals(replacementB, pool.Spawn(secondPrefab)), "both new leases must remain owned and reusable");
            }
            finally
            {
                pool.Dispose();
                UnityEngine.Object.DestroyImmediate(firstPrefab);
                UnityEngine.Object.DestroyImmediate(secondPrefab);
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Spawn_DespawnedDuringOnSpawned_DoesNotReturnAnInactiveLease()
        {
            var prefab = new GameObject("PoolSpawnReturn_" + Guid.NewGuid().ToString("N"));
            using var pool = new ObjectPoolService();
            try
            {
                var instance = pool.Spawn(prefab);
                var callback = instance.AddComponent<Callback>();
                pool.Despawn(instance);
                callback.OnBegin = () => pool.Despawn(instance);
                bool rejected = false;
                try { pool.Spawn(prefab); }
                catch (InvalidOperationException) { rejected = true; }
                callback.OnBegin = null;
                Require(rejected && instance && !instance.activeSelf, "interrupted acquisition must not return an inactive lease");
                Require(ReferenceEquals(instance, pool.Spawn(prefab)), "the callback-returned instance remains reusable");
            }
            finally { pool.Dispose(); UnityEngine.Object.DestroyImmediate(prefab); }
        }
    }
}
