using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Lifecycle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The DI and lifecycle defects the audit #206 found: a child context ran its parent's lifecycle (#207), a Singleton
    /// startable a scene bound after the root started never started (#208), a cycle through a binding shared by two keys
    /// froze Resolve for 10 s (#209), AsImplementedInterfaces dropped a game interface under "Systems." (#210), a
    /// WithParameter singleton was rebuilt by every scene that bound it (#211), and a stop that outran the teardown
    /// timeout left no line (#212).
    /// </summary>
    public sealed class DiAuditDefectTests
    {
        public sealed class CountingLifecycle : IContextLifecycle
        {
            public int Configures, Inits, Starts, Disposes;
            public void OnConfigure(IContextBuilder builder) => Configures++;
            public ValueTask OnInitializeAsync(CancellationToken ct) { Inits++; return default; }
            public ValueTask OnStartAsync(CancellationToken ct) { Starts++; return default; }
            public void OnDispose() => Disposes++;
        }

        public sealed class LateStartable : IStartable
        {
            public static int Starts;
            public void Start() => Starts++;
        }

        public interface IFirst { }
        public interface ISecond { }
        public sealed class SharedCycle : IFirst, ISecond
        {
            public SharedCycle(ISecond self) { }
        }

        public sealed class Config { }
        public sealed class Configured
        {
            public readonly Config Config;
            public Configured(Config config) => Config = config;
        }

        public sealed class StopThatNeverEnds : IAsyncStoppable
        {
            public ValueTask StopAsync(CancellationToken ct) => new ValueTask(new TaskCompletionSource<bool>().Task);
        }

        private static ContextData NewData()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            return data;
        }

        [Test]
        public async Task AChildContextWithoutALifecycle_LeavesItsParentsLifecycleAlone()
        {
            var lifecycle = new CountingLifecycle();
            var parent = ContextFactory.Create(null, NewData()).OwnsContextData();
            try
            {
                parent.Container.BindInstance<IContextLifecycle>(lifecycle, disposeWithContainer: false);
                parent.Configure(new IContextLifecycle[] { lifecycle });

                var child = ContextFactory.Create(parent, NewData()).OwnsContextData();
                child.Configure();
                await child.InitializeLifecycleAsync(child.ConfiguredLifecycles, CancellationToken.None);
                Assert.AreEqual(0, child.ConfiguredLifecycles.Count);
                child.Dispose();

                Assert.AreEqual((1, 0, 0, 0), (lifecycle.Configures, lifecycle.Inits, lifecycle.Starts, lifecycle.Disposes));
            }
            finally { parent.Dispose(); }
        }

        [Test]
        public async Task ASingletonStartableASceneBindsAfterTheRootStarted_StartsOnce()
        {
            LateStartable.Starts = 0;
            var global = ContextFactory.Create(null, NewData()).OwnsContextData();
            try
            {
                global.Configure();
                await global.InitializeLifecycleAsync(global.ConfiguredLifecycles, CancellationToken.None);

                for (int load = 0; load < 2; load++)
                {
                    var scene = ContextFactory.Create(global, NewData()).OwnsContextData();
                    scene.GetOrCreateBuilder().Bind<LateStartable>(Lifetime.Singleton);
                    scene.Configure();
                    await scene.InitializeLifecycleAsync(scene.ConfiguredLifecycles, CancellationToken.None);
                    scene.Dispose();
                }

                Assert.AreEqual(1, LateStartable.Starts, "started by the first scene, kept and not started again by the reloaded one");
            }
            finally { global.Dispose(); }
        }

        [Test]
        public void ACycleThroughABindingSharedByTwoKeys_FailsAtOnceAsACycle()
        {
            using var container = new NexusDI();
            container.BindMultiple<IFirst, ISecond, SharedCycle>(Lifetime.Scoped);
            var clock = Stopwatch.StartNew();

            var error = Assert.Throws<InvalidOperationException>(() => container.Resolve<IFirst>());

            StringAssert.Contains("Circular dependency", error.Message);
            Assert.Less(clock.Elapsed.TotalSeconds, 5, "it waited on its own construction");
        }

        [Test]
        public void AsImplementedInterfaces_BindsAGameInterfaceUnderANamespaceThatStartsWithSystem()
        {
            using var container = new NexusDI();
            container.BindFluent<Systems.Audit210.Score>().AsScoped().AsImplementedInterfaces();

            Assert.IsTrue(container.IsRegistered(typeof(Systems.Audit210.IScore)));
            Assert.IsFalse(container.IsRegistered(typeof(IEquatable<Systems.Audit210.Score>)), "an interface under System. stays out");
        }

        [Test]
        public void ASingletonBoundAgainWithEqualParameters_KeepsItsInstance_AndDifferentOnesBuildANewOne()
        {
            using var root = new NexusDI();
            var config = new Config();
            Configured first, second, third;
            using (var scene = root.CreateChildScope(s => s.BindFluent<Configured>().AsSingleton().WithParameter(config))) first = scene.Resolve<Configured>();
            // WithParameter first: its Scoped step must not stay behind in the child and shadow the root's singleton
            using (var scene = root.CreateChildScope(s => s.BindFluent<Configured>().WithParameter(config).AsSingleton())) second = scene.Resolve<Configured>();
            Assert.AreSame(first, second);
            LogAssert.NoUnexpectedReceived();

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape($"Rebinding '{typeof(Configured).FullName}'")));
            var other = new Config();
            using (var scene = root.CreateChildScope(s => s.BindFluent<Configured>().AsSingleton().WithParameter(other))) third = scene.Resolve<Configured>();
            Assert.AreSame(other, third.Config);
        }

        [Test]
        public async Task AStopAsyncStillRunningAtTheTeardownTimeout_IsReported()
        {
            var warnings = new ConcurrentQueue<string>();
            Application.LogCallback capture = (message, stack, type) => { if (type == LogType.Warning) warnings.Enqueue(message); };
            Application.logMessageReceivedThreaded += capture;
            try
            {
                new ContextLifecycleOrchestrator().ExecuteStoppableLifecyclesSync(new object[] { new StopThatNeverEnds() });
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && !warnings.Any(w => w.Contains(nameof(StopThatNeverEnds)) && w.Contains("did not finish within 5 s"))) await Task.Delay(100);
            }
            finally { Application.logMessageReceivedThreaded -= capture; }

            Assert.IsTrue(warnings.Any(w => w.Contains(nameof(StopThatNeverEnds)) && w.Contains("did not finish within 5 s")),
                string.Join("\n", warnings));
            // #291: the synchronous Dispose names the stop that outlives it before it disposes the singletons
            Assert.IsTrue(warnings.Any(w => w.Contains(nameof(StopThatNeverEnds)) && w.Contains("is still running while the context disposes its singletons")),
                string.Join("\n", warnings));
        }
    }
}

namespace Systems.Audit210
{
    public interface IScore { }
    public sealed class Score : IScore, IEquatable<Score>
    {
        public bool Equals(Score other) => ReferenceEquals(this, other);
    }
}
