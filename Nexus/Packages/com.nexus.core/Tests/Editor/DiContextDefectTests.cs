using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #197: a context disposed its services before the singletons that use them; a Lifetime.Singleton bound again
    /// by a reloaded scene built a second instance; a typed-factory argument reached only a parameter of its exact type
    /// and an explicit value no parameter took was dropped; MockContext.TryResolve ignored its container; the test harness leaked its ContextData; a game type
    /// named TimerTickSignal was never traced.
    /// </summary>
    public sealed class DiContextDefectTests
    {
        private static readonly List<string> s_disposed = new();

        public sealed class StartupLifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        public sealed class EagerService : INexusService
        {
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() => s_disposed.Add(nameof(EagerService));
        }

        public sealed class LazyService : INexusService
        {
            public readonly EagerService Eager;
            public LazyService(EagerService eager) => Eager = eager;
            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void OnDispose() => s_disposed.Add(nameof(LazyService));
        }

        public sealed class ServiceConsumer : IDisposable
        {
            public readonly LazyService Service;
            public ServiceConsumer(LazyService service) => Service = service;
            public void Dispose() => s_disposed.Add(nameof(ServiceConsumer));
        }

        public sealed class SceneSingleton
        {
            public static int Constructed;
            public SceneSingleton() => Constructed++;
        }

        public sealed class ChildOnlyDependency { }

        public sealed class SingletonNeedingAChildBinding
        {
            public SingletonNeedingAChildBinding(ChildOnlyDependency dependency) { }
        }

        public interface IEnemy { }
        public sealed class Enemy : IEnemy { }

        public sealed class EnemyMarker
        {
            public readonly IEnemy Enemy;
            public EnemyMarker(IEnemy enemy) => Enemy = enemy;
        }

        public sealed class Dependency { }

        public sealed class NeedsDependency
        {
            public NeedsDependency(Dependency dependency) { }
        }

        [SetUp]
        public void ResetProbes()
        {
            s_disposed.Clear();
            SceneSingleton.Constructed = 0;
        }

        [Test]
        public async Task AContext_DisposesItsSingletonsInReverseCreationOrder_AConsumerBeforeTheServicesItUses()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            data.AssemblyScopes = Array.Empty<string>();
            var context = ContextFactory.Create(null, data).OwnsContextData();
            try
            {
                var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
                builder.BindService<EagerService, EagerService>();
                builder.BindLazyService<LazyService>();
                builder.Bind<ServiceConsumer>();
                context.ConfigureWithBuilder(builder, new IContextLifecycle[] { new StartupLifecycle() }, scanAssemblies: false);
                await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);
                Assert.IsNotNull(context.Container.Resolve<ServiceConsumer>().Service.Eager);
            }
            finally { context.Dispose(); }

            CollectionAssert.AreEqual(new[] { nameof(ServiceConsumer), nameof(LazyService), nameof(EagerService) }, s_disposed);
        }

        [Test]
        public void ALifetimeSingletonBoundAgainByTheNextChild_KeepsItsOneInstance_WithoutAWarning()
        {
            using var root = new NexusDI();
            var firstScene = root.CreateChildScope(scope => scope.Bind<SceneSingleton>(Lifetime.Singleton));
            var first = firstScene.Resolve<SceneSingleton>();
            firstScene.Dispose();

            using var secondScene = root.CreateChildScope(scope => scope.Bind<SceneSingleton>(Lifetime.Singleton));
            Assert.AreSame(first, secondScene.Resolve<SceneSingleton>());
            Assert.AreEqual(1, SceneSingleton.Constructed);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ALifetimeSingletonThatNeedsABindingOnlyItsChildHas_IsReported()
        {
            using var root = new NexusDI();
            using var scene = root.CreateChildScope(scope =>
            {
                scope.Bind<ChildOnlyDependency>();
                scope.Bind<SingletonNeedingAChildBinding>(Lifetime.Singleton);
            });

            var issues = scene.ValidateBindings();
            Assert.IsTrue(issues.Any(issue => issue.IssueType == DiValidationIssueType.MissingConstructorDependency
                && issue.Message.Contains(typeof(ChildOnlyDependency).FullName)), string.Join("\n", issues.Select(issue => issue.Message)));
        }

        [Test]
        public void ATypedFactoryArgument_FillsTheInterfaceParameterItImplements()
        {
            using var container = new NexusDI();
            container.BindTypedFactory<Enemy, EnemyMarker>();
            var enemy = new Enemy();

            var marker = container.Resolve<IFactory<Enemy, EnemyMarker>>().Create(enemy);

            Assert.AreSame(enemy, marker.Enemy);
        }

        [Test]
        public void AnExplicitValueNoConstructorParameterTakes_FailsTheResolve_AndIsReported()
        {
            using var container = new NexusDI();
            container.Bind<Dependency>();
            container.BindFluent<NeedsDependency>().AsTransient().WithParameter("unused 197");

            var error = Assert.Throws<InvalidOperationException>(() => container.Resolve<NeedsDependency>());
            StringAssert.Contains(typeof(string).FullName, error.Message);
            Assert.IsTrue(container.ValidateBindings().Any(issue => issue.IssueType == DiValidationIssueType.InvalidBinding
                && issue.Message.Contains(typeof(NeedsDependency).FullName)));
        }

        [Test]
        public void MockContext_TryResolve_ReturnsWhatItsContainerHolds()
        {
            using var container = new NexusDI();
            var dependency = new Dependency();
            var named = new Dependency();
            container.BindInstance(dependency);
            container.BindInstance("named", named);
            var mock = new MockContext { Container = container };

            Assert.AreSame(dependency, mock.TryResolve<Dependency>());
            Assert.AreSame(named, mock.TryResolve<Dependency>("named"));
            Assert.IsNull(new MockContext().TryResolve<Dependency>());
        }

        [Test]
        public void TheTestHarness_DestroysTheContextDataItCreates()
        {
            int before = Resources.FindObjectsOfTypeAll<ContextData>().Length;
            using (var parent = NexusTestHarness.CreateContext("Harness197"))
            using (NexusTestHarness.CreateChildContext(parent)) { }

            Assert.AreEqual(before, Resources.FindObjectsOfTypeAll<ContextData>().Length);
        }

        [Test]
        public void AGameSignalNamedTimerTickSignal_IsTraced()
        {
#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, "TimerTickSignal");
            try { Assert.Greater(eventId, 0); }
            finally { NexusTrace.EndEvent(eventId); }
#else
            Assert.Ignore("Tracing runs only with NEXUS_DEBUG.");
#endif
        }
    }
}
