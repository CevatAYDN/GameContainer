using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #190: a lazy injection initialized a service the start-up had already initialized, and a child context its
    /// parent's; a cancelled or disposed start-up still ran the game's lifecycle; a child disposed before it resolved a
    /// service disposed its parent's service of that key; a startable singleton nothing resolved never started, and one
    /// that threw left the context started.
    /// </summary>
    public sealed class LifecycleContractTests
    {
        public interface IProbeService { }

        public sealed class ProbeService : IProbeService, INexusService
        {
            public static int Initialized, Disposed;
            public ValueTask InitializeAsync(CancellationToken ct) { Initialized++; return default; }
            public void OnDispose() => Disposed++;
        }

        public sealed class ProbeHost
        {
            [Inject] public LazyInjection<IProbeService> Probe;
        }

        public sealed class StepLog : IContextLifecycle
        {
            public int Initialized, Started;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) { Initialized++; return default; }
            public ValueTask OnStartAsync(CancellationToken ct) { Started++; return default; }
            public void OnDispose() { }
        }

        public sealed class StoppingService : INexusService
        {
            public static Action Stop;
            public ValueTask InitializeAsync(CancellationToken ct) { Stop?.Invoke(); return default; }
            public void OnDispose() { }
        }

        public sealed class UnresolvedStartable : IStartable
        {
            public static int Started;
            public void Start() => Started++;
        }

        public sealed class LazyStartableService : INexusService, IStartable
        {
            public static int Constructed;
            public LazyStartableService() => Constructed++;
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() { }
            public void Start() { }
        }

        public sealed class ThrowingStartable : IAsyncStartable
        {
            public ValueTask StartAsync(CancellationToken ct) => throw new InvalidOperationException("startable bug");
        }

        [SetUp]
        public void ResetProbes()
        {
            ProbeService.Initialized = ProbeService.Disposed = 0;
            UnresolvedStartable.Started = 0;
            LazyStartableService.Constructed = 0;
            StoppingService.Stop = null;
        }

        private static Context Configure(Action<ContextBuilder> bind, Context parent = null, params IContextLifecycle[] lifecycles)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            data.AssemblyScopes = Array.Empty<string>();
            var context = ContextFactory.Create(parent, data).OwnsContextData();
            var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
            bind(builder);
            context.ConfigureWithBuilder(builder, lifecycles, scanAssemblies: false);
            return context;
        }

        private static async Task<Exception> StartAsync(Context context, CancellationToken ct)
        {
            try { await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, ct); }
            catch (Exception ex) { return ex; }
            return null;
        }

        [Test]
        public async Task ALazyInjectionOfAServiceTheStartupInitialized_DoesNotInitializeItAgain()
        {
            var context = Configure(b => { b.BindService<IProbeService, ProbeService>(); b.Bind<ProbeHost>(); });
            try
            {
                Assert.IsNull(await StartAsync(context, CancellationToken.None));
                Assert.IsNotNull(context.Container.Resolve<ProbeHost>().Probe.Value);
                await context.InitializeLazyServicesAsync(CancellationToken.None);
                Assert.AreEqual(1, ProbeService.Initialized);
            }
            finally { context.Dispose(); }
        }

        [Test]
        public async Task AChildsLazyInjectionOfItsParentsService_DoesNotInitializeItAgain()
        {
            var parent = Configure(b => b.BindService<IProbeService, ProbeService>());
            Context child = null;
            try
            {
                Assert.IsNull(await StartAsync(parent, CancellationToken.None));
                child = Configure(b => b.Bind<ProbeHost>(), parent);
                Assert.IsNull(await StartAsync(child, CancellationToken.None));
                Assert.IsNotNull(child.Container.Resolve<ProbeHost>().Probe.Value);
                await child.InitializeLazyServicesAsync(CancellationToken.None);
                await parent.InitializeLazyServicesAsync(CancellationToken.None);
                Assert.AreEqual(1, ProbeService.Initialized);
            }
            finally
            {
                child?.Dispose();
                parent.Dispose();
            }
        }

        [Test]
        public async Task AStartupCancelledWhileAServiceInitializes_StopsBeforeTheGamesLifecycle()
        {
            using var cts = new CancellationTokenSource();
            StoppingService.Stop = cts.Cancel;
            var steps = new StepLog();
            var context = Configure(b => b.BindService<StoppingService, StoppingService>(), null, steps);
            try
            {
                Assert.IsInstanceOf<OperationCanceledException>(await StartAsync(context, cts.Token));
                Assert.AreEqual(0, steps.Initialized, "OnInitializeAsync ran on a cancelled start-up");
                Assert.AreEqual(0, steps.Started, "OnStartAsync ran on a cancelled start-up");
            }
            finally { context.Dispose(); }
        }

        [Test]
        public async Task AContextDisposedWhileAServiceInitializes_StopsItsStartupWithACancellation()
        {
            var steps = new StepLog();
            Context context = null;
            context = Configure(b => b.BindService<StoppingService, StoppingService>(), null, steps);
            StoppingService.Stop = () => context.Dispose();
            Exception stopped;
            try { stopped = await StartAsync(context, CancellationToken.None); }
            finally { context.Dispose(); }
            // a cancellation is Root's quiet path; any other exception is logged as a failed start
            Assert.IsInstanceOf<OperationCanceledException>(stopped);
            Assert.AreEqual(0, steps.Initialized, "OnInitializeAsync ran on a disposed context");
        }

        [Test]
        public async Task AChildDisposedBeforeItResolvedAService_LeavesTheParentsServiceOfThatKeyAlone()
        {
            var parent = Configure(b => b.BindService<IProbeService, ProbeService>());
            try
            {
                Assert.IsNull(await StartAsync(parent, CancellationToken.None));
                var child = Configure(b => b.BindService<IProbeService, ProbeService>(), parent);
                child.Dispose();
                Assert.AreEqual(0, ProbeService.Disposed, "the child disposed its parent's live service");
            }
            finally { parent.Dispose(); }
        }

        [Test]
        public async Task AStartableSingletonNothingResolved_StartsWithTheContext_WhileALazyServiceStaysUnbuilt()
        {
            var context = Configure(b => { b.Bind<UnresolvedStartable>(); b.BindLazyService<LazyStartableService>(); });
            try
            {
                Assert.IsNull(await StartAsync(context, CancellationToken.None));
                Assert.AreEqual(1, UnresolvedStartable.Started);
                Assert.AreEqual(0, LazyStartableService.Constructed, "the start-up built a lazy service");
            }
            finally { context.Dispose(); }
        }

        [Test]
        public async Task AStartableThatThrows_FailsTheStartup()
        {
            var context = Configure(b => b.Bind<ThrowingStartable>());
            Exception failure;
            try { failure = await StartAsync(context, CancellationToken.None); }
            finally { context.Dispose(); }
            Assert.IsInstanceOf<InvalidOperationException>(failure);
            Assert.AreEqual("startable bug", failure.Message);
        }
    }
}
