using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class MultiContextApiTests
    {
        [Test]
        public void GetContext_ReturnsMatchingScopeContext()
        {
            var gameplayData = ScriptableObject.CreateInstance<ContextData>();
            gameplayData.ScopeTag = "Gameplay";
            using var gameplay = new Context(null, gameplayData);

            var uiData = ScriptableObject.CreateInstance<ContextData>();
            uiData.ScopeTag = "UI";
            using var ui = new Context(null, uiData);

            Assert.AreSame(gameplay, NexusRuntime.GetContext("Gameplay"));
            Assert.AreSame(ui, NexusRuntime.GetContext("UI"));

            Object.DestroyImmediate(gameplayData);
            Object.DestroyImmediate(uiData);
        }

        [Test]
        public void GetContexts_ReturnsAllMatchingScopes()
        {
            var aData = ScriptableObject.CreateInstance<ContextData>();
            aData.ScopeTag = "Shared";
            using var a = new Context(null, aData);

            var bData = ScriptableObject.CreateInstance<ContextData>();
            bData.ScopeTag = "Shared";
            using var b = new Context(null, bData);

            var cData = ScriptableObject.CreateInstance<ContextData>();
            cData.ScopeTag = "Other";
            using var c = new Context(null, cData);

            var contexts = NexusRuntime.GetContexts("Shared");

            Assert.That(contexts, Has.Member(a));
            Assert.That(contexts, Has.Member(b));
            Assert.That(contexts, Has.No.Member(c));

            Object.DestroyImmediate(aData);
            Object.DestroyImmediate(bData);
            Object.DestroyImmediate(cData);
        }

        [Test]
        public void ActiveContexts_SnapshotCannotMutateRegistry()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            using var context = new Context(null, data);

            var snapshot = NexusRuntime.ActiveContexts;
            Assert.IsFalse(snapshot is List<IContext>, "Public snapshot must not expose the mutable cache list.");
            Assert.IsInstanceOf<IList<IContext>>(snapshot);
            Assert.Throws<System.NotSupportedException>(() => ((IList<IContext>)snapshot).Add(context));
            Assert.That(NexusRuntime.ActiveContexts, Has.Member(context));

            Object.DestroyImmediate(data);
        }
    }
}
