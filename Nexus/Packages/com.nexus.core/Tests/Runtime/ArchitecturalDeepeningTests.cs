using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
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
        public async Task SignalBus_FireAsync_AwaitsAYieldingAsyncSubscriber()
        {
            using var context = ContextFactory.Create();
            var signalBus = context.SignalBus;

            bool handlerCalled = false;
            using var sub = signalBus.SubscribeAsync<TestSignal>(async (s, ct) =>
            {
                await Task.Yield();
                handlerCalled = true;
            });

            await signalBus.FireAsync(new TestSignal());
            Assert.IsTrue(handlerCalled);
        }

        [Test]
        public void SignalBus_SubscriptionNodeNested_UnsubscribeCleanup()
        {
            // the warnings this test causes, in the order it causes them (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\]\ Signal\ 'Nexus\.Tests\.Runtime\.ArchitecturalDeepeningTests\+TestSignal'\ fired\ but\ has\ no\ subscribers\ or\ co"));
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

        [UnityTest]
        public IEnumerator ViewRegistration_MonoBehaviourView_WithRootInScene_Registers()
        {
            var owner = new GameObject("ViewRegistrationOwner");
            var data = CreateScopeData("ViewRegistrationTest");
            try
            {
                owner.SetActive(false);
                var root = owner.AddComponent<Root>();
                root.SetUp(data);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the root has no lifecycle
                var view = owner.AddComponent<RegistrationProbeView>();
                owner.SetActive(true);
                yield return null;
                Root pending = null;
                ViewRegistration.Register(view, ref pending);
                Assert.AreSame(root.Context, view.BoundContext);
                Assert.IsNull(pending);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        // ─── Phase 5: QueueDrainer MetricsSampler ───

        [UnityTest]
        public IEnumerator QueueDrainer_WithoutRoot_DisablesItself()
        {
            var owner = new GameObject("QueueWithoutRoot");
            try
            {
                LogAssert.Expect(LogType.Error, "[Nexus] QueueDrainer requires a Root component on the same GameObject.");
                var drainer = owner.AddComponent<QueueDrainer>();
                yield return null;
                Assert.IsFalse(drainer.enabled);
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
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

        public class RegularInjectable
        {
            [Inject] public IDependency Dependency;
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

    public sealed class RegistrationProbeView : MonoBehaviour, IView
    {
        public IContext BoundContext { get; private set; }
        public void Bind(IContext context) => BoundContext = context;
        public void Unbind() => BoundContext = null;
    }

}
