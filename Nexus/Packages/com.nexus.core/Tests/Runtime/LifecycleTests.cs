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
            public bool StartCalled;
            public bool DisposeCalled;

            public void OnConfigure(IContextBuilder builder)
            {
                ConfigureCalled = true;
            }

            public async ValueTask OnInitializeAsync(CancellationToken ct)
            {
                await Task.Delay(10, ct);
                InitializeCalled = true;
            }

            public async ValueTask OnStartAsync(CancellationToken ct)
            {
                await Task.Delay(10, ct);
                StartCalled = true;
            }

            public void OnDispose()
            {
                DisposeCalled = true;
            }
        }

        [Test]
        public async Task ContextLifecycle_TriggeredInOrder()
        {
            var lifecycle = new TestLifecycle();
            var context = new Context();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);

            context.Configure();
            Assert.IsTrue(lifecycle.ConfigureCalled);
            Assert.IsFalse(lifecycle.InitializeCalled);

            await lifecycle.OnInitializeAsync(context.LifetimeToken);
            Assert.IsTrue(lifecycle.InitializeCalled);
            Assert.IsFalse(lifecycle.StartCalled);

            await lifecycle.OnStartAsync(context.LifetimeToken);
            Assert.IsTrue(lifecycle.StartCalled);

            context.Dispose();
            Assert.IsTrue(lifecycle.DisposeCalled);
        }
    }
}
