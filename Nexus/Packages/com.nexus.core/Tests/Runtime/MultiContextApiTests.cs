using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests
{
    [TestFixture]
    public class MultiContextApiTests
    {
        [Test]
        public void GetContext_ReturnsMatchingScopeContext()
        {
            using var gameplay = new Context(null, new ContextData { ScopeTag = "Gameplay" });
            using var ui = new Context(null, new ContextData { ScopeTag = "UI" });

            Assert.AreSame(gameplay, NexusRuntime.GetContext("Gameplay"));
            Assert.AreSame(ui, NexusRuntime.GetContext("UI"));
        }

        [Test]
        public void GetContexts_ReturnsAllMatchingScopes()
        {
            using var a = new Context(null, new ContextData { ScopeTag = "Shared" });
            using var b = new Context(null, new ContextData { ScopeTag = "Shared" });

            var contexts = NexusRuntime.GetContexts("Shared");

            Assert.GreaterOrEqual(contexts.Count, 2);
        }
    }
}
