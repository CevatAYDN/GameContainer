using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Components;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Tests.Runtime
{
    public sealed class InstantiateProbeDependency { }

    public sealed class InstantiateProbe : MonoBehaviour
    {
        [Inject] public InstantiateProbeDependency Dependency { get; set; }
        // Diagnostics must not be serialized: Unity's Instantiate copies serialized fields from the
        // template to the clone, which would carry the template's own Awake/PostConstruct history over.
        [System.NonSerialized] public bool AwakeRan;
        [System.NonSerialized] public bool DependencyWasSetInAwake;
        [System.NonSerialized] public int PostConstructCalls;
        private void Awake() { AwakeRan = true; DependencyWasSetInAwake = Dependency != null; }
        [PostConstruct] public void Ready() => PostConstructCalls++;
    }

    /// <summary>
    /// Instantiating through the container must inject every component of the clone, inactive children
    /// included, before Unity runs their Awake, and must leave the prefab and the clone in their original
    /// active state. A NexusBinding on the prefab must not inject its targets a second time afterwards.
    /// </summary>
    public sealed class ContextInstantiateTests
    {
        private readonly List<GameObject> _objects = new();

        private GameObject Track(GameObject go) { _objects.Add(go); return go; }

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private GameObject BuildTemplate()
        {
            var template = Track(new GameObject("template"));
            template.AddComponent<InstantiateProbe>();
            var child = new GameObject("inactive child");
            child.transform.SetParent(template.transform);
            child.AddComponent<InstantiateProbe>();
            child.SetActive(false);
            return template;
        }

        [Test]
        public void InstantiateInjectsEveryComponentBeforeAwakeAndKeepsActiveStates()
        {
            using var di = new NexusDI();
            di.Bind<InstantiateProbeDependency>();
            var template = BuildTemplate();

            var clone = Track(di.Instantiate(template));

            var root = clone.GetComponent<InstantiateProbe>();
            Assert.That(root.AwakeRan, Is.True);
            Assert.That(root.DependencyWasSetInAwake, Is.True, "injection must precede Awake");
            var child = clone.transform.GetChild(0).GetComponent<InstantiateProbe>();
            Assert.That(child.Dependency, Is.Not.Null, "inactive children are injected too");
            Assert.That(child.AwakeRan, Is.False, "an inactive child's Awake is still deferred by Unity");
            Assert.That(template.activeSelf, Is.True, "the prefab's active state is restored");
            Assert.That(clone.activeSelf, Is.True);
        }

        [Test]
        public void ComponentOverloadReturnsTheCloneComponentUnderTheParent()
        {
            using var di = new NexusDI();
            di.Bind<InstantiateProbeDependency>();
            var template = BuildTemplate();
            var parent = Track(new GameObject("parent"));

            var probe = di.Instantiate(template.GetComponent<InstantiateProbe>(), parent.transform);
            Track(probe.gameObject);

            Assert.That(probe.gameObject, Is.Not.SameAs(template));
            Assert.That(probe.transform.parent, Is.SameAs(parent.transform));
            Assert.That(probe.DependencyWasSetInAwake, Is.True);
        }

        [Test]
        public async Task NexusBindingOnThePrefabDoesNotInjectASecondTime()
        {
            var context = await NexusRuntime.CreatePureContextAsync("InstantiateScope", new[] { "Assembly-CSharp" });
            try
            {
                context.Container.Bind<InstantiateProbeDependency>();
                var template = BuildTemplate();
                var binding = template.AddComponent<NexusBinding>();
                binding.Scope = InjectionScope.Hierarchy;
                binding.Time = InjectionTime.Awake;

                var clone = Track(context.Instantiate(template));

                var root = clone.GetComponent<InstantiateProbe>();
                Assert.That(root.DependencyWasSetInAwake, Is.True);
                Assert.That(root.PostConstructCalls, Is.EqualTo(1), "the binding's Awake must not inject the hierarchy again");
            }
            finally { context.Dispose(); }
        }
    }
}
