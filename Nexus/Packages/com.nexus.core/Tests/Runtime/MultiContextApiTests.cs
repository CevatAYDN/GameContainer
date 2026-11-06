using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class MultiContextApiTests
    {
        private readonly List<ContextData> _data = new List<ContextData>();

        // GetContext returns the first registered context with a tag, so a context another fixture leaked would answer instead
        [SetUp]
        public void SetUp() => NexusRuntime.Reset();

        // The assets are destroyed after the test's `using` contexts are disposed, and also when an assert fails
        [TearDown]
        public void TearDown()
        {
            foreach (var data in _data) Object.DestroyImmediate(data);
            _data.Clear();
        }

        private ContextData NewData(string scopeTag = null)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            if (scopeTag != null) data.ScopeTag = scopeTag;
            _data.Add(data);
            return data;
        }

        [Test]
        public void GetContext_ReturnsMatchingScopeContext()
        {
            using var gameplay = new Context(null, NewData("Gameplay"));
            using var ui = new Context(null, NewData("UI"));

            Assert.AreSame(gameplay, NexusRuntime.GetContext("Gameplay"));
            Assert.AreSame(ui, NexusRuntime.GetContext("UI"));
        }

        [Test]
        public void GetContexts_ReturnsAllMatchingScopes()
        {
            using var a = new Context(null, NewData("Shared"));
            using var b = new Context(null, NewData("Shared"));
            using var c = new Context(null, NewData("Other"));

            var contexts = NexusRuntime.GetContexts("Shared");

            Assert.That(contexts, Has.Member(a));
            Assert.That(contexts, Has.Member(b));
            Assert.That(contexts, Has.No.Member(c));
        }

        [Test]
        public void ActiveContexts_SnapshotCannotMutateRegistry()
        {
            using var context = new Context(null, NewData());

            var snapshot = NexusRuntime.ActiveContexts;
            Assert.IsFalse(snapshot is List<IContext>, "Public snapshot must not expose the mutable cache list.");
            Assert.IsInstanceOf<IList<IContext>>(snapshot);
            Assert.Throws<System.NotSupportedException>(() => ((IList<IContext>)snapshot).Add(context));
            Assert.That(NexusRuntime.ActiveContexts, Has.Member(context));
        }
    }
}
