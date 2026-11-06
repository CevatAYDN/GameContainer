using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    [TestFixture]
    [Category("RequiresPlayMode")]
    public class SiblingInitializationTests
    {
        private List<string> _initOrder;

        private class TestLifecycleComponent : MonoBehaviour, IContextLifecycle
        {
            public string Name;
            public List<string> LogList;
            public int DelayMs = 50;

            public void OnConfigure(IContextBuilder builder) { }

            public async ValueTask OnInitializeAsync(CancellationToken ct)
            {
                LogList.Add($"{Name}_InitStart");
                await Task.Delay(DelayMs, ct);
                LogList.Add($"{Name}_InitEnd");
            }

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                LogList.Add($"{Name}_Start");
                return default;
            }

            public void OnDispose() { }
        }

        [SetUp]
        public void Setup()
        {
            _initOrder = new List<string>();
            Root.ClearRegistry();
        }

        [TearDown]
        public void TearDown()
        {
            Root.ClearRegistry();
            var roots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Include);
            foreach (var r in roots)
            {
                if (r != null && r.gameObject != null)
                {
                    GameObject.DestroyImmediate(r.gameObject);
                }
            }
        }

        private void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = typeof(Root).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }

        // Waits on the unscaled clock until the roots have logged `count` entries: a fixed WaitForSeconds ended
        // early when one editor frame was slow, and the order tests failed at random (#198). The cap stays well
        // under Root's 15 s sibling timeout, so a root that waits for a sibling it should skip still fails.
        private IEnumerator WaitForEntries(int count, float timeoutSeconds = 5f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (_initOrder.Count < count && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        [UnityTest]
        public IEnumerator SiblingRoots_InitializeInPriorityOrder()
        {
            // Create Hierarchy
            var parentGo = new GameObject("ParentRoot");
            parentGo.SetActive(false);
            var parentRoot = parentGo.AddComponent<Root>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the parent root has no lifecycle

            // Create Sibling A (Lower Priority, but we want it to wait)
            var childGoA = new GameObject("ChildA");
            childGoA.transform.SetParent(parentGo.transform);
            var lifecycleA = childGoA.AddComponent<TestLifecycleComponent>();
            lifecycleA.Name = "ChildA";
            lifecycleA.LogList = _initOrder;
            var rootA = childGoA.AddComponent<Root>();
            SetPrivateField(rootA, "parentRoot", parentRoot);
            SetPrivateField(rootA, "initializationPriority", 5);

            // Create Sibling B (Higher Priority, should run first)
            var childGoB = new GameObject("ChildB");
            childGoB.transform.SetParent(parentGo.transform);
            var lifecycleB = childGoB.AddComponent<TestLifecycleComponent>();
            lifecycleB.Name = "ChildB";
            lifecycleB.LogList = _initOrder;
            var rootB = childGoB.AddComponent<Root>();
            SetPrivateField(rootB, "parentRoot", parentRoot);
            SetPrivateField(rootB, "initializationPriority", 10);

            // Activate game objects to trigger Awake/Start
            parentGo.SetActive(true);

            yield return WaitForEntries(6);

            // B (priority 10) must initialize and start before A (priority 5) starts initializing
            // Expected sequence of logs:
            // "ChildB_InitStart", "ChildB_InitEnd", "ChildB_Start", "ChildA_InitStart", "ChildA_InitEnd", "ChildA_Start"
            Assert.AreEqual(6, _initOrder.Count);
            Assert.AreEqual("ChildB_InitStart", _initOrder[0]);
            Assert.AreEqual("ChildB_InitEnd", _initOrder[1]);
            Assert.AreEqual("ChildB_Start", _initOrder[2]);
            Assert.AreEqual("ChildA_InitStart", _initOrder[3]);
            Assert.AreEqual("ChildA_InitEnd", _initOrder[4]);
            Assert.AreEqual("ChildA_Start", _initOrder[5]);

            // Clean up
            UnityEngine.Object.DestroyImmediate(parentGo);
        }

        [UnityTest]
        public IEnumerator SiblingRoots_TieBreakerAlphabeticalOrder()
        {
            var parentGo = new GameObject("ParentRoot");
            parentGo.SetActive(false);
            var parentRoot = parentGo.AddComponent<Root>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the parent root has no lifecycle

            // Create Sibling B (Name: SiblingB, priority = 0)
            var childGoB = new GameObject("SiblingB");
            childGoB.transform.SetParent(parentGo.transform);
            var lifecycleB = childGoB.AddComponent<TestLifecycleComponent>();
            lifecycleB.Name = "SiblingB";
            lifecycleB.LogList = _initOrder;
            var rootB = childGoB.AddComponent<Root>();
            SetPrivateField(rootB, "parentRoot", parentRoot);
            SetPrivateField(rootB, "initializationPriority", 0);

            // Create Sibling A (Name: SiblingA, priority = 0)
            // SiblingA is alphabetically earlier, so it should run first
            var childGoA = new GameObject("SiblingA");
            childGoA.transform.SetParent(parentGo.transform);
            var lifecycleA = childGoA.AddComponent<TestLifecycleComponent>();
            lifecycleA.Name = "SiblingA";
            lifecycleA.LogList = _initOrder;
            var rootA = childGoA.AddComponent<Root>();
            SetPrivateField(rootA, "parentRoot", parentRoot);
            SetPrivateField(rootA, "initializationPriority", 0);

            parentGo.SetActive(true);

            yield return WaitForEntries(6);

            // SiblingA (earlier alphabetically) must start/finish first
            Assert.AreEqual(6, _initOrder.Count);
            Assert.AreEqual("SiblingA_InitStart", _initOrder[0]);
            Assert.AreEqual("SiblingA_InitEnd", _initOrder[1]);
            Assert.AreEqual("SiblingA_Start", _initOrder[2]);
            Assert.AreEqual("SiblingB_InitStart", _initOrder[3]);
            Assert.AreEqual("SiblingB_InitEnd", _initOrder[4]);
            Assert.AreEqual("SiblingB_Start", _initOrder[5]);

            UnityEngine.Object.DestroyImmediate(parentGo);
        }

        [UnityTest]
        public IEnumerator SiblingRoots_SkipsInactiveSiblingImmediately()
        {
            var parentGo = new GameObject("ParentRoot");
            parentGo.SetActive(false);
            var parentRoot = parentGo.AddComponent<Root>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the parent root has no lifecycle

            var childGoA = new GameObject("ChildA");
            childGoA.transform.SetParent(parentGo.transform);
            var lifecycleA = childGoA.AddComponent<TestLifecycleComponent>();
            lifecycleA.Name = "ChildA";
            lifecycleA.LogList = _initOrder;
            var rootA = childGoA.AddComponent<Root>();
            SetPrivateField(rootA, "parentRoot", parentRoot);
            SetPrivateField(rootA, "initializationPriority", 0);

            var childGoB = new GameObject("ChildB");
            childGoB.transform.SetParent(parentGo.transform);
            var rootB = childGoB.AddComponent<Root>();
            SetPrivateField(rootB, "parentRoot", parentRoot);
            SetPrivateField(rootB, "initializationPriority", 10);
            childGoB.SetActive(false);

            parentGo.SetActive(true);
            yield return WaitForEntries(3);

            Assert.That(_initOrder.Count, Is.EqualTo(3), "only ChildA logs: ChildB has no lifecycle and is inactive");
            Assert.AreEqual("ChildA_InitStart", _initOrder[0]);
            Assert.AreEqual("ChildA_InitEnd", _initOrder[1]);
            Assert.AreEqual("ChildA_Start", _initOrder[2]);

            UnityEngine.Object.DestroyImmediate(parentGo);
        }

        [UnityTest]
        public IEnumerator SiblingRoots_DoesNotDoubleInitializeWhenActivatedTwice()
        {
            var parentGo = new GameObject("ParentRoot");
            parentGo.SetActive(false);
            var parentRoot = parentGo.AddComponent<Root>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the parent root has no lifecycle

            var childGo = new GameObject("ChildA");
            childGo.transform.SetParent(parentGo.transform);
            var lifecycle = childGo.AddComponent<TestLifecycleComponent>();
            lifecycle.Name = "ChildA";
            lifecycle.LogList = _initOrder;
            lifecycle.DelayMs = 10;
            var childRoot = childGo.AddComponent<Root>();
            SetPrivateField(childRoot, "parentRoot", parentRoot);
            SetPrivateField(childRoot, "initializationPriority", 0);

            parentGo.SetActive(true);
            yield return WaitForEntries(3);

            parentGo.SetActive(false);
            parentGo.SetActive(true);
            // a second initialization would log its InitStart on the re-enable, before its delay
            for (int i = 0; i < 10; i++) yield return null;

            var initStarts = 0;
            foreach (var entry in _initOrder)
            {
                if (entry == "ChildA_InitStart") initStarts++;
            }

            Assert.AreEqual(1, initStarts);

            UnityEngine.Object.DestroyImmediate(parentGo);
        }
    }
}
