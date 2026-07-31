using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Regression tests for the adversarial-review fixes applied to the Runtime layer:
    ///  1. ContextBuilder.Validate now validates concrete implementations (not just binding keys)
    ///     and never flags LazyInjection&lt;T&gt; fields (which the injector constructs directly).
    ///  2. Services are disposed exactly once: the owning Context calls OnDispose, and the
    ///     container skips INexusService singletons (no double-dispose via IDisposable).
    ///  3. A lazy service first resolved during OnStartAsync still receives InitializeAsync
    ///     (second drain after OnStartAsync).
    /// </summary>
    [TestFixture]
    public class AdversarialReviewFixTests
    {
        // ─── Fix 1: ContextBuilder.Validate concrete-type validation ───

        public interface ISomeService { }
        public interface IUnregisteredDep { }
        public class SomeService : ISomeService
        {
            public SomeService(IUnregisteredDep dep) { }
        }

        public class LazyHost
        {
            [Inject] public LazyInjection<LazyInitService> Lazy;
        }

        public class LazyInitService : INexusService
        {
            public static int InitCount;
            public ValueTask InitializeAsync(CancellationToken ct) { InitCount++; return default; }
            public void OnDispose() { }
        }

        [Test]
        public void Validate_ReportsMissingCtorDependency_OnConcreteImplementation()
        {
            using var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            using var bus = new SignalBus(container, poolManager, new MockContext());
            var builder = new ContextBuilder(container, bus);

            // Key is the interface; the concrete type's ctor dependency used to be unchecked.
            builder.Bind<ISomeService, SomeService>();

            var issues = builder.Validate();

            Assert.IsTrue(issues.Any(i =>
                    i.SourceType == typeof(SomeService) &&
                    i.IssueType == DiValidationIssueType.MissingConstructorDependency),
                "Validate must report the concrete implementation's missing ctor dependency.");
        }

        [Test]
        public void Validate_DoesNotFlagLazyInjectionFields()
        {
            using var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            using var bus = new SignalBus(container, poolManager, new MockContext());
            var builder = new ContextBuilder(container, bus);

            builder.Bind<LazyHost>();
            builder.Bind<LazyInitService>();

            var issues = builder.Validate();

            Assert.IsFalse(issues.Any(i => i.SourceType == typeof(LazyHost)),
                "LazyInjection<T> fields are constructed by the injector and must not be flagged.");
        }

        // ─── Fix 2: service double-dispose ───

        public class CountingService : NexusService<CountingService>
        {
            public int DisposeCount;
            public override void Dispose() => DisposeCount++;
        }

        public class ServiceLifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) => builder.BindService<CountingService>();
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        [Test]
        public void NexusDI_Dispose_SkipsInNexusServiceSingletons()
        {
            using var container = new NexusDI();
            container.Bind<CountingService>(isSingleton: true);

            var service = container.Resolve<CountingService>();
            container.Dispose();

            // Service lifecycle is owned by the owning Context; a bare container must not
            // dispose it a second time through IDisposable.
            Assert.AreEqual(0, service.DisposeCount);
        }

        [Test]
        public async Task Context_Dispose_DisposesServiceExactlyOnce()
        {
            var context = ContextFactory.Create();
            try
            {
                var lifecycle = new ServiceLifecycle();
                context.Container.BindInstance<IContextLifecycle>(lifecycle);
                context.Configure();
                await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);

                var service = context.Container.Resolve<CountingService>();
                context.Dispose();

                // OnDispose() → Dispose() runs once via the Context; the container skip
                // prevents a second call through IDisposable.
                Assert.AreEqual(1, service.DisposeCount);
            }
            finally
            {
                context.Dispose(); // idempotent (_disposed guard)
                NexusRuntime.Reset();
            }
        }

        // ─── Fix 3: lazy service resolved during OnStartAsync is still initialized ───

        public class LazyResolvingLifecycle : IContextLifecycle
        {
            public NexusDI Container; // assigned by the test
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                // First resolution happens DURING OnStartAsync — the window the second
                // drain in InitializeLifecycleAsync exists to cover.
                var host = Container.Resolve<LazyHost>();
                _ = host.Lazy.Value;
                return default;
            }

            public void OnDispose() { }
        }

        [Test]
        public async Task LazyService_ResolvedDuringOnStartAsync_IsInitialized()
        {
            LazyInitService.InitCount = 0;
            var context = ContextFactory.Create();
            try
            {
                var lifecycle = new LazyResolvingLifecycle { Container = context.Container };
                context.Container.BindInstance<IContextLifecycle>(lifecycle);
                context.Container.Bind<LazyHost>(isSingleton: true);
                context.Container.Bind<LazyInitService>(isSingleton: true);

                context.Configure();
                await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);

                Assert.AreEqual(1, LazyInitService.InitCount,
                    "A lazy service first resolved during OnStartAsync must still be initialized.");
            }
            finally
            {
                context.Dispose();
                NexusRuntime.Reset();
            }
        }
    }
}
