using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    /// <summary>Issue #66: a lifecycle reaches the container through [Inject], and ISignalPublisher resolves.</summary>
    [TestFixture]
    public class LifecycleInjectionTests
    {
        public sealed class LifecycleDependency { }

        private sealed class InjectedLifecycle : IContextLifecycle
        {
            [Inject] public LifecycleDependency Dependency { get; set; }
            [Inject] public ISignalPublisher Signals { get; set; }
            public bool DependencyInConfigure;
            public bool DependencyInInitialize;
            public bool SignalsInStart;

            public void OnConfigure(IContextBuilder builder)
            {
                DependencyInConfigure = Dependency != null;
                builder.Bind<LifecycleDependency>(Lifetime.Singleton);
            }

            public ValueTask OnInitializeAsync(CancellationToken ct)
            {
                DependencyInInitialize = Dependency != null;
                return default;
            }

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                SignalsInStart = Signals != null;
                return default;
            }

            public void OnDispose() { }
        }

        [Test]
        public async Task LifecycleInjectMembers_AreSetBeforeOnInitializeAsync()
        {
            var lifecycle = new InjectedLifecycle();
            var context = new Context();
            try
            {
                context.Configure(new IContextLifecycle[] { lifecycle });
                await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, context.LifetimeToken);

                Assert.IsFalse(lifecycle.DependencyInConfigure, "OnConfigure runs before the bindings exist.");
                Assert.IsTrue(lifecycle.DependencyInInitialize, "[Inject] members must be set before OnInitializeAsync.");
                Assert.IsTrue(lifecycle.SignalsInStart, "ISignalPublisher must be injectable into a lifecycle.");
            }
            finally { context.Dispose(); }
        }

        [Test]
        public async Task SignalPublisherAndSubscriber_ResolveToTheContextSignalBus()
        {
            var context = await ContextFactory.StartAsync("signal-interfaces", _ => { });
            try
            {
                Assert.AreSame(context.SignalBus, context.Container.TryResolve<ISignalPublisher>());
                Assert.AreSame(context.SignalBus, context.Container.TryResolve<ISignalSubscriber>());
            }
            finally { await context.DisposeAsync(); }
        }
    }
}
