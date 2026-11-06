using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests
{
    [TestFixture]
    public class LifecycleTests
    {
        private class TestLifecycle : IContextLifecycle
        {
            public bool ConfigureCalled;
            public bool InitializeCalled;
            public bool DisposeCalled;

            public void OnConfigure(IContextBuilder builder)
            {
                ConfigureCalled = true;
            }

            // Configure and Dispose only: the test never initializes or starts the context (ArchitecturalDeepeningTests runs the full lifecycle)
            public ValueTask OnInitializeAsync(CancellationToken ct)
            {
                InitializeCalled = true;
                return default;
            }

            public ValueTask OnStartAsync(CancellationToken ct) => default;

            public void OnDispose()
            {
                DisposeCalled = true;
            }
        }

        [Test]
        public void Configure_AndDispose_ReachTheLifecycleBoundInTheContextsContainer()
        {
            var lifecycle = new TestLifecycle();
            var context = new Context();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);

            context.Configure();
            Assert.IsTrue(lifecycle.ConfigureCalled);
            Assert.IsFalse(lifecycle.InitializeCalled, "Configure does not initialize");

            context.Dispose();
            Assert.IsTrue(lifecycle.DisposeCalled);
        }
    }
}
