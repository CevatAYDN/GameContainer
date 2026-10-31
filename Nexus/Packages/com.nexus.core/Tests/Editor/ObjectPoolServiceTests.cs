using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class ObjectPoolServiceTests
    {
        private class TestPoolableComponent : MonoBehaviour, IPoolable
        {
            public bool SpawnedCalled { get; private set; }
            public bool DespawnedCalled { get; private set; }

            public void OnSpawned()
            {
                SpawnedCalled = true;
                DespawnedCalled = false;
            }

            public void OnDespawned()
            {
                DespawnedCalled = true;
            }
        }

        private GameObject _prefab;
        private ObjectPoolService _poolService;
        private GameObject _manualRoot;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("TestPrefab");
            _prefab.AddComponent<TestPoolableComponent>();

            // Create manual root to avoid DontDestroyOnLoad in EditMode
            _manualRoot = new GameObject("[Nexus_ObjectPool_Test]");
            
            _poolService = new ObjectPoolService();
            // Skip InitializeAsync to avoid DontDestroyOnLoad
            // Manually set up the root
            var poolServiceType = typeof(ObjectPoolService);
            var rootField = poolServiceType.GetField("_masterRootObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var transformField = poolServiceType.GetField("_masterPoolRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (rootField != null)
                rootField.SetValue(_poolService, _manualRoot);
            if (transformField != null)
                transformField.SetValue(_poolService, _manualRoot.transform);
        }

        [TearDown]
        public void TearDown()
        {
            // Manual cleanup without calling Dispose (which uses Destroy)
            if (_manualRoot != null)
            {
                Object.DestroyImmediate(_manualRoot);
                _manualRoot = null;
            }
            
            if (_prefab != null)
            {
                Object.DestroyImmediate(_prefab);
            }
        }

        [Test]
        public void Prewarm_InstantiatesRequestedCount()
        {
            _poolService.Prewarm(_prefab, 5);
            var spawned = _poolService.Spawn(_prefab);

            Assert.IsNotNull(spawned);
            Assert.AreEqual(_prefab.name, spawned.name);
            _poolService.Despawn(spawned);
        }

        [Test]
        public void SpawnAndDespawn_TriggersIPoolableCallbacks()
        {
            var instance = _poolService.Spawn<TestPoolableComponent>(_prefab.GetComponent<TestPoolableComponent>());
            Assert.IsNotNull(instance);
            Assert.IsTrue(instance.SpawnedCalled);

            _poolService.Despawn(instance.gameObject);
            Assert.IsTrue(instance.DespawnedCalled);
            Assert.IsFalse(instance.gameObject.activeSelf);
        }

        [Test]
        public void SpawnSessionGenerations_AdvanceOnRespawn_GuardStaleTimers()
        {
            // Regression: DespawnAfter used to capture only the instance and blindly despawn
            // it when the timer fired. If the object was manually despawned and RE-spawned
            // while the timer was pending, the stale timer killed the live re-spawned object.
            // The fix tracks a per-instance spawn-session generation: it advances on every
            // Spawn and is cleared on Despawn, so stale timers can detect the re-spawn.
            // The generation map was re-keyed from GetInstanceID ints to managed object
            // references (ObjectPoolService.GetKey) — the test must use the same key space.
            var genField = typeof(ObjectPoolService).GetField("_spawnGenerations",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var generations = (System.Collections.Generic.Dictionary<UnityEngine.Object, long>)genField.GetValue(_poolService);

            var instance = _poolService.Spawn(_prefab);
            Assert.IsTrue(generations.TryGetValue(instance, out long firstGen), "Spawn must record a generation.");

            // Manual despawn clears the generation entry (no stale timer can hit it).
            _poolService.Despawn(instance);
            Assert.IsFalse(generations.ContainsKey(instance), "Despawn must clear the generation entry.");

            // Re-spawn gets a NEW, higher generation.
            var respawned = _poolService.Spawn(_prefab);
            Assert.AreSame(instance, respawned, "Pool should reuse the same instance.");
            Assert.IsTrue(generations.TryGetValue(instance, out long secondGen));
            Assert.Greater(secondGen, firstGen,
                "Re-spawn must advance the generation so stale DespawnAfter timers are ignored.");

            _poolService.Despawn(respawned);
        }
        private T Field<T>(string name) => (T)typeof(ObjectPoolService)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(_poolService);

        // Issue #184: the first Spawn's parent became the pool root, so the despawned instances died with the level
        // and the next Spawn popped a destroyed object (MissingReferenceException).
        [Test]
        public void ThePoolKeepsItsInstances_WhenTheFirstSpawnsParentIsDestroyed()
        {
            var level = new GameObject("Level");
            var first = _poolService.Spawn(_prefab, default, default, level.transform);
            Assert.AreSame(level.transform, first.transform.parent, "the spawn parent applies to the active instance");
            _poolService.Despawn(first);
            Object.DestroyImmediate(level);

            GameObject second = null;
            Assert.DoesNotThrow(() => second = _poolService.Spawn(_prefab));
            Assert.AreSame(first, second, "the despawned instance waited under the service's root, not under the level");
            _poolService.Despawn(second);
        }

        // Issue #184 (and the 2026-10-31 review's B2): an instance destroyed while it waited in the pool poisoned it,
        // because every Spawn popped it again.
        [Test]
        public void Spawn_SkipsAnInstanceDestroyedWhileItWaitedInThePool()
        {
            var first = _poolService.Spawn(_prefab);
            _poolService.Despawn(first);
            Object.DestroyImmediate(first);

            GameObject second = null;
            Assert.DoesNotThrow(() => second = _poolService.Spawn(_prefab));
            Assert.IsTrue(second != null && second.activeSelf);
            _poolService.Despawn(second);
        }

        // Issue #184: active instances destroyed with their parent stayed in the pool's maps for good.
        [Test]
        public void ActiveInstancesDestroyedWithTheirParent_AreUnregistered_WhenThePoolGrows()
        {
            var level = new GameObject("Level");
            for (int i = 0; i < 3; i++) _poolService.Spawn(_prefab, default, default, level.transform);
            Object.DestroyImmediate(level);

            var fresh = _poolService.Spawn(_prefab);

            Assert.AreEqual(1, Field<Dictionary<Object, long>>("_spawnGenerations").Count);
            Assert.AreEqual(1, Field<System.Collections.IDictionary>("_poolsByInstanceId").Count);
            _poolService.Despawn(fresh);
        }

        // Issue #184: Prewarm beyond the per-pool cap instantiated every instance and destroyed the surplus, silently.
        [Test]
        public void Prewarm_BeyondThePoolCap_KeepsTheCapAndWarns()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"Prewarm.*'TestPrefab'.*130.*128"));
            _poolService.Prewarm(_prefab, 130);

            var poolRoot = _manualRoot.transform.Find("Pool_TestPrefab");
            Assert.AreEqual(128, poolRoot.childCount);
        }

        // Issue #184 (and the review's B4): DespawnAfter before InitializeAsync or after Dispose did nothing, silently,
        // so the object stayed in the scene.
        [Test]
        public void DespawnAfter_WithoutAnInitializedService_DespawnsNowAndWarns()
        {
            var uninitialized = new ObjectPoolService();
            var instance = uninitialized.Spawn(_prefab);
            try
            {
                LogAssert.Expect(LogType.Warning, new Regex(@"DespawnAfter.*'TestPrefab'.*despawned now"));
                uninitialized.DespawnAfter(instance, 5f);
                Assert.IsFalse(instance.activeSelf);
            }
            finally
            {
                uninitialized.ClearAllPools();
            }
        }
    }
}
