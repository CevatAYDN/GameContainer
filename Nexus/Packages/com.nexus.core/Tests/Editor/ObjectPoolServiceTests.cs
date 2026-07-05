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

        [SetUp]
        public async Task SetUp()
        {
            _prefab = new GameObject("TestPrefab");
            _prefab.AddComponent<TestPoolableComponent>();

            _poolService = new ObjectPoolService();
            await _poolService.InitializeAsync(default);
        }

        [TearDown]
        public void TearDown()
        {
            _poolService.Dispose();
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
