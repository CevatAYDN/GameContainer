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
    }
}
