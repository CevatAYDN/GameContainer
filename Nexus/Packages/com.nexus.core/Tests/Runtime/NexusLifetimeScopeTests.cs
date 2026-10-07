using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Core.Tests
{
    public sealed class NexusLifetimeScopeTests
    {
        [UnityTest]
        public IEnumerator Awake_AutoDiscoversParentScope_AndOwnsChildContext()
        {
            NexusRuntime.Reset();
            var parent = new GameObject("ParentScope");
            var child = new GameObject("ChildScope");
            try
            {
                parent.SetActive(false);
                child.SetActive(false);
                child.transform.SetParent(parent.transform);
                var parentScope = parent.AddComponent<NexusLifetimeScope>();
                var childScope = child.AddComponent<NexusLifetimeScope>();
                parent.SetActive(true);
                child.SetActive(true);
                yield return null;
                Assert.IsNotNull(parentScope.Context);
                Assert.IsNotNull(childScope.Context);
                Assert.AreSame(parentScope, childScope.ParentScope);
                Assert.AreSame(parentScope.Context, childScope.Context.Parent);
            }
            finally
            {
                Object.DestroyImmediate(child);
                Object.DestroyImmediate(parent);
                NexusRuntime.Reset();
            }
        }
    }
}
