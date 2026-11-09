using System;
using System.Collections;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    public sealed class FullReviewCompositionC05Tests
    {
        [UnityTest]
        public IEnumerator FailedClone_NeverAwakesOrEnables_AndIsDestroyed()
        {
            var previousTemplate = CloneProbe.Template;
            int previousAwakes = CloneProbe.CloneAwakes, previousEnables = CloneProbe.CloneEnables;
            var template = new GameObject("composition C05 native template");
            CloneProbe.Template = template;
            CloneProbe.CloneAwakes = CloneProbe.CloneEnables = 0;
            template.AddComponent<CloneProbe>();
            var adapter = new FailingInjection();
            using var container = new NexusDI { ExternalAdapter = adapter };
            try
            {
                var failure = Assert.Throws<InvalidOperationException>(() => container.Instantiate(template));
                Assert.AreSame(adapter.Failure, failure);
                Assert.IsFalse(ReferenceEquals(adapter.Clone, null), "Injection must have reached a real clone.");
                Assert.IsTrue(template.activeSelf, "Restore the template on failure.");
                Assert.AreEqual(0, CloneProbe.CloneAwakes, "Failed injection must never activate the clone.");
                Assert.AreEqual(0, CloneProbe.CloneEnables, "Failed injection must never enable clone behaviours.");
                yield return null;
                Assert.IsFalse(adapter.Clone, "Destroy the unreturned clone by the next frame.");
            }
            finally
            {
                if (adapter.Clone) UnityEngine.Object.DestroyImmediate(adapter.Clone);
                UnityEngine.Object.DestroyImmediate(template);
                CloneProbe.Template = previousTemplate;
                CloneProbe.CloneAwakes = previousAwakes;
                CloneProbe.CloneEnables = previousEnables;
            }
        }

        private sealed class CloneProbe : MonoBehaviour
        {
            public static GameObject Template;
            public static int CloneAwakes, CloneEnables;
            private void Awake()
            {
                if (!ReferenceEquals(gameObject, Template)) CloneAwakes++;
            }
            private void OnEnable()
            {
                if (!ReferenceEquals(gameObject, Template)) CloneEnables++;
            }
        }

        private sealed class FailingInjection : IDependencyAdapter
        {
            public readonly InvalidOperationException Failure = new("native clone injection probe");
            public GameObject Clone;
            public object Resolve(Type type) => null;
            public bool IsRegistered(Type type) => false;
            public void Inject(object instance)
            {
                if (instance is not CloneProbe component) return;
                Clone = component.gameObject;
                throw Failure;
            }
        }
    }
}
