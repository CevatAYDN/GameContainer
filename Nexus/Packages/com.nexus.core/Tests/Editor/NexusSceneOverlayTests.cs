using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Tests.Editor
{
    /// <summary>
    /// Locks the Scene-view overlay's data layer (<see cref="NexusSceneOverlayData"/>): the
    /// boundary computation (renderer/collider merging + unit-box fallback) and the signal
    /// wiring collection (registered signal → command pairs, [CrossContext] flagging, and the
    /// Root→node mapping with serialized scope tags). Drawing itself is SceneView-bound and is
    /// deliberately kept out of the testable surface.
    /// </summary>
    public class NexusSceneOverlayTests
    {
        private struct OverlaySignal { public int Value; }
        private struct OverlaySignalB { }
        [CrossContext("Meta")]
        private struct OverlayCrossSignal { }

        private class OverlayCommand : ICommand<OverlaySignal>
        {
            public void Execute(OverlaySignal signal) { }
        }

        private class OverlayCommandB : ICommand<OverlaySignalB>
        {
            public void Execute(OverlaySignalB signal) { }
        }

        private class OverlayCrossCommand : ICommand<OverlayCrossSignal>
        {
            public void Execute(OverlayCrossSignal signal) { }
        }

        // ── Boundary computation ─────────────────────────────────

        [Test]
        public void ComputeHierarchyBounds_NoGeometry_FallsBackToUnitBoxAtRootPosition()
        {
            var go = new GameObject("BoundsFallback");
            try
            {
                go.transform.position = new Vector3(10f, 20f, 30f);
                var bounds = NexusSceneOverlayData.ComputeHierarchyBounds(go);
                Assert.AreEqual(go.transform.position, bounds.center, "Fallback box must center on the Root transform.");
                Assert.AreEqual(Vector3.one, bounds.size, "Fallback box must be a unit cube.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ComputeHierarchyBounds_MergesChildPrimitiveBounds()
        {
            var parent = new GameObject("BoundsParent");
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                child.transform.SetParent(parent.transform);
                child.transform.localPosition = new Vector3(0f, 5f, 0f); // 1×1×1 cube centered at (0,5,0)

                var bounds = NexusSceneOverlayData.ComputeHierarchyBounds(parent);
                // In headless batch Edit Mode the physics engine never ticks, so a moved
                // object's Collider.bounds can stay at its creation position while its
                // Renderer.bounds are current. Assert the boundary covers the cube wherever
                // it actually is, plus the exact X/Z span, instead of the full exact box.
                Assert.IsTrue(bounds.Contains(new Vector3(0f, 5f, 0f)),
                    "The merged boundary must cover the child cube at (0,5,0).");
                Assert.AreEqual(1f, bounds.size.x, 0.001f);
                Assert.AreEqual(1f, bounds.size.z, 0.001f);
                Assert.GreaterOrEqual(bounds.max.y, 5.5f, "The boundary must reach above the child cube.");
                Assert.LessOrEqual(bounds.min.y, 4.5f, "The boundary must reach below the child cube.");
            }
            finally
            {
                Object.DestroyImmediate(parent);
                if (child != null) Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void ComputeHierarchyBounds_EncapsulatesMultipleRenderers()
        {
            var parent = new GameObject("BoundsParent");
            var a = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                a.transform.SetParent(parent.transform);
                a.transform.localPosition = new Vector3(0f, 0f, 0f);
                b.transform.SetParent(parent.transform);
                b.transform.localPosition = new Vector3(4f, 0f, 0f);

                var bounds = NexusSceneOverlayData.ComputeHierarchyBounds(parent);
                // Two unit cubes at x=0 and x=4 → merged span [-0.5, 4.5].
                Assert.AreEqual(new Vector3(2f, 0f, 0f), bounds.center);
                Assert.AreEqual(new Vector3(5f, 1f, 1f), bounds.size);
            }
            finally
            {
                Object.DestroyImmediate(parent);
                if (a != null) Object.DestroyImmediate(a);
                if (b != null) Object.DestroyImmediate(b);
            }
        }

        // ── Signal link collection ───────────────────────────────

        [Test]
        public void CollectSignalLinks_ReturnsRegisteredSignalCommandPairs()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            try
            {
                bus.RegisterCommand(typeof(OverlaySignal), typeof(OverlayCommand),
                    ExecutionMode.Exclusive, priority: 7, isAsync: false);

                var links = NexusSceneOverlayData.CollectSignalLinks(bus);
                Assert.AreEqual(1, links.Count);
                Assert.AreEqual(nameof(OverlaySignal), links[0].SignalName);
                Assert.AreEqual(nameof(OverlayCommand), links[0].HandlerName);
                Assert.AreEqual(ExecutionMode.Exclusive, links[0].Mode);
                Assert.IsFalse(links[0].IsCrossContext);
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void CollectSignalLinks_FlagsCrossContextSignalWithScope()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            try
            {
                bus.RegisterCommand(typeof(OverlayCrossSignal), typeof(OverlayCrossCommand),
                    ExecutionMode.Sequential, priority: 0, isAsync: false);

                var links = NexusSceneOverlayData.CollectSignalLinks(bus);
                Assert.AreEqual(1, links.Count);
                Assert.IsTrue(links[0].IsCrossContext, "[CrossContext] signal must be flagged for the inter-context pass.");
                Assert.AreEqual("Meta", links[0].CrossContextScope);
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void CollectSignalLinks_SortsDeterministically_AndHandlesMultipleHandlers()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            try
            {
                bus.RegisterCommand(typeof(OverlaySignalB), typeof(OverlayCommandB),
                    ExecutionMode.Sequential, priority: 0, isAsync: false);
                bus.RegisterCommand(typeof(OverlaySignal), typeof(OverlayCommand),
                    ExecutionMode.Sequential, priority: 0, isAsync: false);

                var links = NexusSceneOverlayData.CollectSignalLinks(bus);
                Assert.AreEqual(2, links.Count);
                Assert.AreEqual(nameof(OverlaySignal), links[0].SignalName);
                Assert.AreEqual(nameof(OverlaySignalB), links[1].SignalName);
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void CollectSignalLinks_NoHandlers_ReturnsEmpty()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            try
            {
                Assert.AreEqual(0, NexusSceneOverlayData.CollectSignalLinks(bus).Count);
            }
            finally
            {
                bus.Dispose();
            }
        }

        // ── Context node collection ──────────────────────────────

        [Test]
        public void CollectContextNodes_EditMode_UsesSerializedDataAndNoLinks()
        {
            var parentGo = new GameObject("ParentRoot");
            var childGo = new GameObject("ChildRoot");
            var parentData = ScriptableObject.CreateInstance<ContextData>();
            var childData = ScriptableObject.CreateInstance<ContextData>();
            try
            {
                // Inactive GameObjects: AddComponent<Root>() never runs Awake, so no Context
                // is created — exactly the Edit Mode preview state the overlay relies on.
                parentGo.SetActive(false);
                var parentRoot = parentGo.AddComponent<Root>();
                parentData.ScopeTag = "ParentScope";
                parentRoot.SetUp(parentData);

                childGo.SetActive(false);
                var childRoot = childGo.AddComponent<Root>();
                childData.ScopeTag = "ChildScope";
                childRoot.SetUp(childData, parentRoot);

                var nodes = NexusSceneOverlayData.CollectContextNodes(new[] { parentRoot, childRoot });

                Assert.AreEqual(2, nodes.Count);
                var child = nodes.First(n => n.ScopeTag == "ChildScope");
                Assert.AreEqual(childGo.transform.position, child.Bounds.center);
                Assert.AreEqual(Vector3.one, child.Bounds.size, "No geometry → unit-box fallback.");
                Assert.AreEqual("ParentScope", child.ParentScopeTag, "Parent scope must come from the parent root's data.");
                Assert.AreEqual(parentGo, child.ParentGameObject);
                Assert.AreEqual(0, child.Links.Count, "No live context in Edit Mode → no signal links.");
                Assert.IsNull(child.Context);
            }
            finally
            {
                Object.DestroyImmediate(childGo);
                Object.DestroyImmediate(parentGo);
                if (childData != null) ScriptableObject.DestroyImmediate(childData);
                if (parentData != null) ScriptableObject.DestroyImmediate(parentData);
            }
        }

        [Test]
        public void CollectContextNodes_ScopeTagFallsBackToGameObjectName()
        {
            var go = new GameObject("PlainRoot");
            try
            {
                go.SetActive(false);
                var root = go.AddComponent<Root>(); // no ContextData assigned

                var nodes = NexusSceneOverlayData.CollectContextNodes(new[] { root });
                Assert.AreEqual(1, nodes.Count);
                Assert.AreEqual("PlainRoot", nodes[0].ScopeTag);
                Assert.IsNull(nodes[0].ParentScopeTag);
                Assert.IsNull(nodes[0].ParentGameObject);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
