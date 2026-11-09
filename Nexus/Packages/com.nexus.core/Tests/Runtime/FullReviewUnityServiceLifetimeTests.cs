using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class FullReviewUnityServiceLifetimeTests
    {
        private readonly List<IDisposable> _services = new List<IDisposable>();
        private readonly List<Object> _objects = new List<Object>();
        private PoolActivationReentryProbe _activation;

        [UnityTearDown]
        public IEnumerator DisposeTestOwnedObjects()
        {
            FullReviewUnityServicePrefabProbe.Activated = null;
            if (_activation != null) _activation.Activated = null;
            _activation = null;
            try
            {
                for (int i = _services.Count - 1; i >= 0; i--) _services[i].Dispose();
            }
            finally
            {
                _services.Clear();
                for (int i = _objects.Count - 1; i >= 0; i--)
                    if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
                _objects.Clear();
            }
            yield return null;
        }

        // U01 / #358: replacement happens before Unity processes the first owner's Destroy.
        [UnityTest]
        public IEnumerator ReplacingTheLastUiManagerInTheSameFrame_PreservesTheReplacementCanvas()
        {
            if (GameObject.Find("[Nexus_UICanvas]") != null)
                Assert.Inconclusive("U01 needs no pre-existing Nexus canvas; another owner's objects are not cleared.");

            var first = new UIManager();
            _services.Add(first);
            first.InitializeAsync(default).GetAwaiter().GetResult();
            Assert.IsTrue(first.CanvasRoot != null, "U01 setup: the first manager must create a canvas.");
            var retiredCanvas = first.CanvasRoot.gameObject;
            _objects.Add(retiredCanvas);

            first.Dispose();
            var replacement = new UIManager();
            _services.Add(replacement);
            replacement.InitializeAsync(default).GetAwaiter().GetResult();
            Assert.IsTrue(replacement.CanvasRoot != null, "U01 setup: the replacement must expose its canvas.");
            _objects.Add(replacement.CanvasRoot.gameObject);

            var content = new GameObject("FullReviewUnityServiceCanvasContent", typeof(RectTransform));
            _objects.Add(content);
            content.transform.SetParent(replacement.CanvasRoot, false);

            var follower = new UIManager();
            _services.Add(follower);
            follower.InitializeAsync(default).GetAwaiter().GetResult();
            Assert.AreSame(replacement.CanvasRoot, follower.CanvasRoot,
                "U01: later owners in the same frame must join the replacement, not create competing canvases.");

            yield return null;

            Assert.IsTrue(retiredCanvas == null, "U01: the last owner's retired canvas must still be destroyed.");
            Assert.IsTrue(replacement.CanvasRoot != null,
                "U01: the live replacement manager lost its canvas at the deferred-destruction boundary.");
            Assert.IsTrue(content != null && content.transform.IsChildOf(replacement.CanvasRoot),
                "U01: content attached through the replacement's CanvasRoot must survive with that owner.");
        }

        // U02 / #360: OnEnable returns the lease, not an IPoolable.OnSpawned callback.
        [UnityTest]
        public IEnumerator ReturningALeaseDuringActivation_RejectsTheSpawnInsteadOfReturningADoomedObject()
        {
            var prefab = new GameObject("FullReviewUnityServiceSelfReturningPrefab");
            _objects.Add(prefab);
            var pool = new ObjectPoolService();
            _services.Add(pool);
            pool.InitializeAsync(default).GetAwaiter().GetResult();

            var lease = pool.Spawn(prefab);
            _objects.Add(lease);
            _activation = lease.AddComponent<PoolActivationReentryProbe>();
            pool.Despawn(lease);
            int activations = 0;
            _activation.Activated = () =>
            {
                activations++;
                pool.Despawn(lease);
            };

            GameObject returned = null;
            Exception failure = null;
            try
            {
                returned = pool.Spawn(prefab);
                if (!ReferenceEquals(returned, null)) _objects.Add(returned);
            }
            catch (Exception ex) { failure = ex; }

            yield return null;

            Assert.AreEqual(1, activations, "U02 setup: the reused instance's OnEnable must run exactly once.");
            Assert.That(failure, Is.TypeOf<InvalidOperationException>(),
                $"U02: self-return during activation must reject the acquisition. " +
                $"Returned managed reference: {!ReferenceEquals(returned, null)}; alive after a frame: {returned != null}.");
            Assert.IsTrue(lease != null && !lease.activeSelf,
                "U02: a self-returned lease must remain pooled instead of being destroyed to reject the acquisition.");
            _activation.Activated = null;
            Assert.AreSame(lease, pool.Spawn(prefab), "U02: the rejected acquisition must remain reusable.");
        }

        [UnityTest]
        public IEnumerator ReturningANewLeaseDuringActivation_RejectsTheSpawnAndPreservesReuse()
        {
            var prefab = new GameObject("FullReviewUnityServiceActivePrefab", typeof(FullReviewUnityServicePrefabProbe));
            _objects.Add(prefab);
            var pool = new ObjectPoolService();
            _services.Add(pool);
            pool.InitializeAsync(default).GetAwaiter().GetResult();
            GameObject activated = null;
            int activations = 0;
            FullReviewUnityServicePrefabProbe.Activated = instance =>
            {
                activated = instance;
                activations++;
                _objects.Add(instance);
                pool.Despawn(instance);
            };

            GameObject returned = null;
            Exception failure = null;
            try
            {
                returned = pool.Spawn(prefab);
                if (!ReferenceEquals(returned, null)) _objects.Add(returned);
            }
            catch (Exception ex) { failure = ex; }

            yield return null;

            Assert.AreEqual(1, activations, "U02 setup: the freshly instantiated prefab must activate once.");
            Assert.That(failure, Is.TypeOf<InvalidOperationException>(),
                "U02: a fresh active prefab needs ownership before its first OnEnable, not only on pool reuse.");
            Assert.IsTrue(activated != null && !activated.activeSelf,
                "U02: the new lease returned from OnEnable must survive in its pool.");
            Assert.AreEqual(0, activated.GetComponent<FullReviewUnityServicePrefabProbe>().SpawnCallbacks,
                "U02: OnSpawned must not run after activation has already returned the lease.");
            FullReviewUnityServicePrefabProbe.Activated = null;
            Assert.AreSame(activated, pool.Spawn(prefab), "U02: the new, interrupted lease must be reusable.");
            Assert.AreEqual(1, activated.GetComponent<FullReviewUnityServicePrefabProbe>().SpawnCallbacks,
                "U02: the later successful acquisition delivers OnSpawned once.");
        }
    }
}
