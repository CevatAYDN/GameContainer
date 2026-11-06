using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    /// <summary>The pool paths that need play mode: DespawnAfter timers and PrewarmAsync (issue #184), and the scene of a
    /// spawn without a parent (issue #227).</summary>
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
            _pool.InitializeAsync(default).GetAwaiter().GetResult();
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
            // Cannot fail reliably: an unwanted despawn can arrive after the fixed wait; no clock seam (#317).
            Assert.IsTrue(reused.activeSelf);
        }

        // DespawnAfter captured only the instance, so the timer of a spawn that was despawned by hand despawned the live
        // object that reused the instance.
        [UnityTest]
        public IEnumerator DespawnAfter_FromAnEarlierSpawn_LeavesTheRespawnedInstanceAlone()
        {
            var fx = _pool.Spawn(_prefab);
            _pool.DespawnAfter(fx, 0.05f);
            _pool.Despawn(fx);
            var respawned = _pool.Spawn(_prefab);
            Assert.AreSame(fx, respawned);

            yield return new WaitForSeconds(0.2f);
            // Cannot fail reliably: the earlier timer can fire after the fixed wait; no clock seam (#317).
            Assert.IsTrue(respawned.activeSelf, "the earlier spawn's timer despawned the respawned instance");

            _pool.DespawnAfter(respawned, 0.05f);
            yield return new WaitForSeconds(0.2f);
            // Cannot fail reliably: a late earlier timer can satisfy this after the fixed wait; no clock seam (#317).
            Assert.IsFalse(respawned.activeSelf, "the current spawn's timer despawns it");
        }

        // A spawn without a parent stayed under the pool root, which lives in DontDestroyOnLoad, so it outlived its level.
        [Test]
        public void Spawn_WithoutAParent_PutsTheInstanceAtTheActiveScenesRoot()
        {
            var enemy = _pool.Spawn(_prefab);

            Assert.IsNull(enemy.transform.parent);
            Assert.AreEqual(SceneManager.GetActiveScene(), enemy.scene);
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
