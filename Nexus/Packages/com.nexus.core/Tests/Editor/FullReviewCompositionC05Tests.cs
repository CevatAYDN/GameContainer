using System;
using Nexus.Core;
using UnityEngine;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewCompositionC05Tests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void FailedPrefabInjection_ReleasesTheUnreturnedCloneAndRestoresTemplate()
        {
            var template = new GameObject("composition C05 template");
            template.AddComponent<CloneProbe>();
            var adapter = new FailingInjection();
            using var container = new NexusDI { ExternalAdapter = adapter };
            try
            {
                Exception failure = null;
                try { container.Instantiate(template); }
                catch (Exception ex) { failure = ex; }
                Require(ReferenceEquals(failure, adapter.Failure), "Injection must preserve the original failure.");
                Require(!ReferenceEquals(adapter.Clone, null), "The regression must fail after creating the clone.");
                Require(!adapter.Clone, "Failed injection left an unreturned clone alive.");
                Require(template.activeSelf, "Failure must restore the template's original active state.");
            }
            finally
            {
                if (adapter.Clone) UnityEngine.Object.DestroyImmediate(adapter.Clone);
                UnityEngine.Object.DestroyImmediate(template);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class CloneProbe : MonoBehaviour
        {
            public CloneProbe() { }
        }

        private sealed class FailingInjection : IDependencyAdapter
        {
            public readonly Exception Failure = new InvalidOperationException("clone injection probe");
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
