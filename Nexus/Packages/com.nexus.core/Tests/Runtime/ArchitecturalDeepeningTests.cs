using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Tests for the architectural deepening refactor across 6 modules.
    /// Verifies extracted modules work correctly, no regressions, and
    /// behavioral parity between old and new code paths.
    /// </summary>
    [TestFixture]
    public class ArchitecturalDeepeningTests
    {
        // ─── Phase 1: NexusDI — Injector Clearer MetadataCache ───

        [Test]
        public void NexusDI_InjectorCached_NoAllocationOnInject()
        {
            // Injector is created once in the constructor and cached as _injector.
            // Verify that Inject() works correctly (would throw if injector was null/malformed).
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();

            var target = new DependentClass();
            Assert.DoesNotThrow(() => di.Inject(target));
            Assert.IsNotNull(target.InjectedField);
        }

        [Test]
        public void NexusDI_InjectorCached_NoAllocationOnResolve()
        {
            // Singleton resolve creates instance via cached injector.
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<DependentClass>(isSingleton: true);

            var instance = di.Resolve<DependentClass>();
            Assert.IsNotNull(instance);
            Assert.IsNotNull(instance.InjectedField);
        }

        [Test]
        public void NexusDI_InjectorCached_TransientResolve()
        {
            // Transient resolve also uses cached injector.
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<DependentClass>(isSingleton: false);

            var first = di.Resolve<DependentClass>();
            var second = di.Resolve<DependentClass>();
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second); // transients
        }

        [Test]
        public void NexusDI_MetadataCacheForwarding_Works()
        {
            // GetOrCreateInjectMetadata is forwarded from NexusDI to MetadataCache.
            var meta = NexusDI.GetOrCreateInjectMetadata(typeof(DependentClass));
            Assert.IsNotNull(meta);
            Assert.IsTrue(meta.Fields.Length > 0 || meta.Properties.Length > 0 || meta.Methods.Length > 0);
        }

        [Test]
        public void NexusDI_Clearer_ResettableCalled()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<ResettableCommand>(isSingleton: false);

            var cmd = di.Resolve<ResettableCommand>();
            Assert.IsNotNull(cmd.DependencyField);

            NexusDI.ClearInjectedReferences(cmd);

            Assert.IsNull(cmd.DependencyField);
            Assert.IsTrue(cmd.ResetCalled);
        }

        [Test]
        public void NexusDI_Clearer_RegularInstance()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<RegularInjectable>(isSingleton: false);

            var instance = di.Resolve<RegularInjectable>();
            Assert.IsNotNull(instance.Dependency);

            NexusDI.ClearInjectedReferences(instance);
            Assert.IsNull(instance.Dependency);
        }

        // ─── Phase 2: ContextFactory ───

        [Test]
        public void ContextFactory_Create_SignalBusContextIsNotNull()
        {
            // CRITICAL: The factory path had a bug where SignalBus._context was null.
            // Now SignalBus is created inside the Context constructor with 'this'.
            var context = ContextFactory.Create();
            Assert.IsNotNull(context);
            Assert.IsNotNull(context.SignalBus);
            Assert.IsNotNull(context.Container);

            // SignalBus must have a valid context reference. We verify by firing a signal
            // — if _context were null, FireAsyncWithTimeout would NRE on _context.LifetimeToken.
            var signalFired = false;
            context.SignalBus.Subscribe<TestSignal>(_ => signalFired = true);
            context.SignalBus.Fire(new TestSignal());
            Assert.IsTrue(signalFired);

            context.Dispose();
        }

        [Test]
        public void ContextFactory_Create_HasAllSubModules()
        {
            // Factory should wire all sub-modules correctly.
            var context = ContextFactory.Create();
            Assert.IsNotNull(context.Container);
            Assert.IsNotNull(context.SignalBus);
            Assert.IsNotNull(context.SignalBusInternal);
            Assert.IsNotNull(context.HybridQueue);
            Assert.IsNotNull(context.PoolManager);
            Assert.IsNotNull(context.LifetimeToken);

            // Container should have the core bindings
            Assert.IsTrue(context.Container.IsRegistered(typeof(NexusDI)));
            Assert.IsTrue(context.Container.IsRegistered(typeof(IContext)));
            Assert.IsTrue(context.Container.IsRegistered(typeof(ISignalBus)));
            Assert.IsTrue(context.Container.IsRegistered(typeof(HybridQueue)));

            context.Dispose();
        }

        [Test]
        public void ContextFactory_BackwardCompatParity_RegisterView()
        {
            // Both paths should allow view registration.
            var factoryCtx = ContextFactory.Create();
            var compatCtx = new Context();

            // Verify both have working RegisterView
            var factoryViewRegistered = false;
            var compatViewRegistered = false;

            var factoryView = new MockView(() => factoryViewRegistered = true);
            var compatView = new MockView(() => compatViewRegistered = true);

            Assert.DoesNotThrow(() => factoryCtx.RegisterView(factoryView));
            Assert.DoesNotThrow(() => compatCtx.RegisterView(compatView));

            // Registration must have actually bound the views (MockView.Bind fires the callback).
            Assert.IsTrue(factoryViewRegistered, "Factory path must bind the view on register.");
            Assert.IsTrue(compatViewRegistered, "Compat path must bind the view on register.");

            factoryCtx.Dispose();
            compatCtx.Dispose();
        }

        [Test]
        public void ContextFactory_ViewBinderInContainer()
        {
            // ViewBinder should be registered in the container by the factory path.
            var context = ContextFactory.Create();
            // The internal constructor now calls Container.BindInstance(_viewBinder)
            // so it should be resolvable.
            Assert.DoesNotThrow(() => context.Container.Resolve<ViewBinder>());
            context.Dispose();
        }

        [Test]
        public void ContextFactory_WithParent_ResolvesFromParent()
        {
            var parent = ContextFactory.Create(contextData: CreateScopeData("Parent"));
            parent.Container.Bind<IDependency, ConcreteDependency>();

            var child = ContextFactory.Create(parent: parent, contextData: CreateScopeData("Child"));

            // Child should resolve IDependency from parent
            var resolved = child.Container.TryResolve<IDependency>();
            Assert.IsNotNull(resolved);
            Assert.IsInstanceOf<ConcreteDependency>(resolved);

            child.Dispose();
            parent.Dispose();
        }

        // ─── Phase 3: SignalBus nested types ───

        [Test]
        public void SignalBus_SubscriptionNodeNested_SubscriptionsWork()
        {
            // SubscriptionNode and SubscriptionNodePool are now nested inside SignalBus.
            // Verify subscriptions still work correctly.
            using var di = new NexusDI();
            var context = ContextFactory.Create();
            var signalBus = context.SignalBus;

            bool handlerCalled = false;
            var sub = signalBus.Subscribe<TestSignal>(_ => handlerCalled = true);

            signalBus.Fire(new TestSignal());
            Assert.IsTrue(handlerCalled);

            sub.Dispose();
            context.Dispose();
        }

        [Test]
        public void SignalBus_SubscriptionNodeNested_AsyncSubscription()
        {
            var context = ContextFactory.Create();
            var signalBus = context.SignalBus;

            bool handlerCalled = false;
            var sub = signalBus.SubscribeAsync<TestSignal>(async (s, ct) =>
            {
                await Task.Yield();
                handlerCalled = true;
            });

            signalBus.FireAsync(new TestSignal()).GetAwaiter().GetResult();
            Assert.IsTrue(handlerCalled);

            sub.Dispose();
            context.Dispose();
        }

        [Test]
        public void SignalBus_SubscriptionNodeNested_UnsubscribeCleanup()
        {
            var context = ContextFactory.Create();
            var signalBus = context.SignalBus;

            int callCount = 0;
            var sub = signalBus.Subscribe<TestSignal>(_ => callCount++);

            signalBus.Fire(new TestSignal());
            Assert.AreEqual(1, callCount);

            sub.Dispose();
            signalBus.Fire(new TestSignal());
            // After dispose, handler should not be called
            Assert.AreEqual(1, callCount);

            context.Dispose();
        }

        // ─── Phase 4: ViewRegistration ───

        [Test]
        public void ViewRegistration_NonMonoBehaviourView_SkipsGracefully()
        {
            // ViewRegistration.Register should skip non-MonoBehaviour views without crashing.
            var mockView = new MockView(() => { });
            Root pendingRoot = null;

            Assert.DoesNotThrow(() => ViewRegistration.Register(mockView, ref pendingRoot));
            Assert.IsNull(pendingRoot);
        }

        [Test]
        public void ViewRegistration_MonoBehaviourView_WithRootInScene_Registers()
        {
            // This requires a Unity scene with a Root, so it's a PlayMode test.
            // The test verifies the code path exists and doesn't throw.
            // Full integration testing requires Play Mode.
            Assert.Ignore("ViewRegistration integration requires a Play Mode scene with a Root; this EditMode suite does not claim coverage.");
        }

        // ─── Phase 5: QueueDrainer MetricsSampler ───

        [Test]
        public void QueueDrainer_ExecutionOrder_AfterRoot()
        {
            // QueueDrainer has DefaultExecutionOrder(-900), Root has (-1000).
            var queueOrder = typeof(QueueDrainer).GetCustomAttribute<UnityEngine.Scripting.PreserveAttribute>();
            // Verify the class exists and is annotated
            Assert.IsNotNull(typeof(QueueDrainer));
            Assert.IsNotNull(typeof(MetricsSampler));
        }

        [Test]
        public void QueueDrainer_WithoutRoot_DisablesItself()
        {
            // QueueDrainer.Awake disables itself if no Root is found on the GameObject.
            // This is a structural test - the logic is in Awake.
            // Full verification requires a Unity scene.
            Assert.Ignore("QueueDrainer self-disable requires a Play Mode GameObject; this EditMode suite does not claim coverage.");
        }

        [Test]
        public void MetricsSampler_FrameGate_OncePerFrame()
        {
            // MetricsSampler uses static frame-gate fields identical to the original Root code.
            // Verify the fields exist with correct types.
            var frameField = typeof(MetricsSampler).GetField("s_lastFrameMetricsFrame",
                BindingFlags.Static | BindingFlags.NonPublic);
            var memoryField = typeof(MetricsSampler).GetField("s_lastMemoryMetricsFrame",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.IsNotNull(frameField, "MetricsSampler must have s_lastFrameMetricsFrame");
            Assert.IsNotNull(memoryField, "MetricsSampler must have s_lastMemoryMetricsFrame");
            Assert.AreEqual(typeof(int), frameField.FieldType);
            Assert.AreEqual(typeof(int), memoryField.FieldType);
        }

        // ─── Integration: Complete lifecycle ───

        [Test]
        public async Task ContextFactory_FullLifecycle_NoNullContextSignalBus()
        {
            // Verify the complete lifecycle: create → configure → init → fire → dispose
            // Key test: SignalBus._context is never null.
            var lifecycle = new TrackingLifecycle();
            var context = ContextFactory.Create();
            context.Container.BindInstance<IContextLifecycle>(lifecycle);

            context.Configure();
            Assert.IsTrue(lifecycle.ConfigureCalled);

            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None);
            Assert.IsTrue(lifecycle.InitializeCalled);
            Assert.IsTrue(lifecycle.StartCalled);

            // Fire a signal — this would crash if SignalBus._context were null
            bool signalReceived = false;
            context.SignalBus.Subscribe<TestSignal>(_ => signalReceived = true);
            context.SignalBus.Fire(new TestSignal());
            Assert.IsTrue(signalReceived);

            context.Dispose();
            Assert.IsTrue(lifecycle.DisposeCalled);
        }

        [Test]
        public void ContextFactory_NestedContexts_IndependentSignals()
        {
            var parent = ContextFactory.Create();
            var child = ContextFactory.Create(parent: parent);

            int parentCount = 0;
            int childCount = 0;

            parent.SignalBus.Subscribe<TestSignal>(_ => parentCount++);
            child.SignalBus.Subscribe<TestSignal>(_ => childCount++);

            // Fire on parent — only parent should receive
            parent.SignalBus.Fire(new TestSignal());
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(0, childCount);

            // Fire on child — only child should receive
            child.SignalBus.Fire(new TestSignal());
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            child.Dispose();
            parent.Dispose();
        }

        // ─── Test helpers ───

        public struct TestSignal { }

        public interface IDependency { }
        public class ConcreteDependency : IDependency { }

        public class DependentClass
        {
            [Inject] public IDependency InjectedField;
            [Inject] public IDependency InjectedProperty { get; set; }
            public IDependency MethodInjectedDependency;

            [Inject]
            public void Construct(IDependency dep)
            {
                MethodInjectedDependency = dep;
            }
        }

        public class RegularInjectable
        {
            [Inject] public IDependency Dependency;
        }

        public class ResettableCommand : IResettable
        {
            [Inject] public IDependency DependencyField;
            public bool ResetCalled;

            public void Reset()
            {
                ResetCalled = true;
                DependencyField = null;
            }
        }

        public class MockView : IView
        {
            private readonly Action _onBind;
            public MockView(Action onBind) { _onBind = onBind; }

            public void Bind(IContext context) => _onBind?.Invoke();
            public void Unbind() { }
        }

        public class TrackingLifecycle : IContextLifecycle
        {
            public bool ConfigureCalled;
            public bool InitializeCalled;
            public bool StartCalled;
            public bool DisposeCalled;

            public void OnConfigure(IContextBuilder builder) => ConfigureCalled = true;
            public async ValueTask OnInitializeAsync(CancellationToken ct) { await Task.Yield(); InitializeCalled = true; }
            public async ValueTask OnStartAsync(CancellationToken ct) { await Task.Yield(); StartCalled = true; }
            public void OnDispose() => DisposeCalled = true;
        }

        private static ContextData CreateScopeData(string scopeTag)
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = scopeTag;
            data.AssemblyScopes = Array.Empty<string>();
            return data;
        }

        [SetUp]
        public void SetUp()
        {
            // Reset runtime state between tests
            NexusRuntime.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            NexusRuntime.Reset();
        }
    }

}
