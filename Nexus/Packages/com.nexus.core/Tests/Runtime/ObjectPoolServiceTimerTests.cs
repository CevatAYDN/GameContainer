using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    /// <summary>The pool paths that need frames: DespawnAfter timers and PrewarmAsync (issue #184).</summary>
    [TestFixture]
    public class ObjectPoolServiceTimerTests
    {
        private ObjectPoolService _pool;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("PooledFx");
            _pool = new ObjectPoolService();
            _pool.InitializeAsync(default);
        }

        [TearDown]
        public void TearDown()
        {
            _pool.Dispose();
            if (_prefab != null) Object.Destroy(_prefab);
        }

        // DespawnAfter on an instance already back in its pool took it for an unpooled object, so the timer despawned
        // whatever live object reused the instance by then.
        [UnityTest]
        public IEnumerator DespawnAfter_OnAnInstanceAlreadyBackInItsPool_LeavesTheObjectThatReusesItAlone()
        {
            var fx = _pool.Spawn(_prefab);
            _pool.Despawn(fx);

            LogAssert.Expect(LogType.Warning, new Regex(@"DespawnAfter.*'PooledFx'.*already back in its pool"));
            _pool.DespawnAfter(fx, 0.05f);
            var reused = _pool.Spawn(_prefab);
            Assert.AreSame(fx, reused);

            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(reused.activeSelf);
        }

        // PrewarmAsync completed successfully after an instance could not be created.
        [UnityTest]
        public IEnumerator PrewarmAsync_FailsItsTask_WhenAnInstanceCannotBeCreated()
        {
            var task = _pool.PrewarmAsync(_prefab, 5, instancesPerFrame: 1);
            Object.DestroyImmediate(_prefab);

            LogAssert.Expect(LogType.Exception, new Regex("MissingReferenceException"));
            float deadline = Time.realtimeSinceStartup + 2f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsTrue(task.IsFaulted, $"status {task.Status}");
        }
    }
}
