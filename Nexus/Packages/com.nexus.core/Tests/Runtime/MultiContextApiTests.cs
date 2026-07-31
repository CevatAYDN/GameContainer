using NUnit.Framework;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    [Ignore("bisect: temporarily excluded to isolate PlayMode hang poison")]
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

            var contexts = NexusRuntime.GetContexts("Shared");

            Assert.GreaterOrEqual(contexts.Count, 2);

            Object.DestroyImmediate(aData);
            Object.DestroyImmediate(bData);
        }
    }
}
