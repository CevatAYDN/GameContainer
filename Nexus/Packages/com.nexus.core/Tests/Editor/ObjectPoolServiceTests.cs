using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
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
            var genField = typeof(ObjectPoolService).GetField("_spawnGenerations",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var generations = (System.Collections.Generic.Dictionary<int, long>)genField.GetValue(_poolService);

            var instance = _poolService.Spawn(_prefab);
            int id = instance.GetHashCode();
            Assert.IsTrue(generations.TryGetValue(id, out long firstGen), "Spawn must record a generation.");

            // Manual despawn clears the generation entry (no stale timer can hit it).
            _poolService.Despawn(instance);
            Assert.IsFalse(generations.ContainsKey(id), "Despawn must clear the generation entry.");

            // Re-spawn gets a NEW, higher generation.
            var respawned = _poolService.Spawn(_prefab);
            Assert.AreSame(instance, respawned, "Pool should reuse the same instance.");
            Assert.IsTrue(generations.TryGetValue(id, out long secondGen));
            Assert.Greater(secondGen, firstGen,
                "Re-spawn must advance the generation so stale DespawnAfter timers are ignored.");

            _poolService.Despawn(respawned);
        }
    }
}
