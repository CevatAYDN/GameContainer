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

        // PrewarmAsync exists so that a large prewarm does not stall one frame; only its failure path had a test (#198).
        [UnityTest]
        public IEnumerator PrewarmAsync_CreatesAtMostInstancesPerFrameEachFrame_AndCompletesWithTheWholeCount()
        {
            var task = _pool.PrewarmAsync(_prefab, 5, instancesPerFrame: 2);
            int previous = PooledInstances(), largestFrame = previous;

            float deadline = Time.realtimeSinceStartup + 2f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                int now = PooledInstances();
                largestFrame = Mathf.Max(largestFrame, now - previous);
                previous = now;
            }

            Assert.That((task.Status, PooledInstances(), largestFrame <= 2), Is.EqualTo((System.Threading.Tasks.TaskStatus.RanToCompletion, 5, true)),
                $"the largest frame created {largestFrame} instances");
        }

        // The pool names its instances after the prefab and keeps them under its own root.
        private int PooledInstances()
        {
            int count = 0;
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
                if (t.name == _prefab.name && t.gameObject != _prefab) count++;
            return count;
        }
    }
}
