using NUnit.Framework;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Components;
using Nexus.Core.Lifecycle;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    public struct TestCapSignal
    {
        public int Amount;
        public TestCapSignal(int amount) => Amount = amount;
    }

    [RegisterCommand(typeof(TestCapSignal))]
    public class TestCapCommand : ICommand<TestCapSignal>
    {
        [Inject] public TestCapState State;
        public void Execute(TestCapSignal signal)
        {
            State.Executions++;
            State.LastAmount = signal.Amount;
        }
    }

    public class TestCapState
    {
        public int Executions;
        public int LastAmount;
        public bool StartSyncCalled;
        public bool StartAsyncCalled;
        public bool StopSyncCalled;
        public bool StopAsyncCalled;
    }

    public interface ITestContractA { string Name { get; } }
    public interface ITestContractB { int Level { get; } }

    public class TestDomainController : ITestContractA, ITestContractB, IStartable, IAsyncStartable, IStoppable, IAsyncStoppable
    {
        [Inject] public TestCapState State;

        public string Name => "DomainController";
        public int Level => 99;

        public void Start() => State.StartSyncCalled = true;
        public ValueTask StartAsync(CancellationToken ct)
        {
            State.StartAsyncCalled = true;
            return default;
        }

        public void Stop() => State.StopSyncCalled = true;
        public ValueTask StopAsync(CancellationToken ct)
        {
            State.StopAsyncCalled = true;
            return default;
        }
    }

    public class TestInjectedComponent : MonoBehaviour
    {
        [System.NonSerialized] [Inject] public TestCapState State;
    }

    [TestFixture]
    public class StrategicCapabilitiesEditModeTests
    {
        [Test]
        public void RegisterCommandAttribute_DecoratesCommandClass_WithSignalTypeAndExecutionMode()
        {
            var attr = typeof(TestCapCommand).GetCustomAttribute<RegisterCommandAttribute>();
            Assert.IsNotNull(attr);
            Assert.AreEqual(typeof(TestCapSignal), attr.SignalType);
            Assert.AreEqual(ExecutionMode.Sequential, attr.Mode);
        }

        [Test]
        public void RegisterCommandAttribute_AutoDiscovery_RegistersAndExecutesCommand()
        {
            var state = new TestCapState();
            using var ctx = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance(state);
                builder.BindCommand<TestCapSignal, TestCapCommand>();
            });

            ctx.Context.SignalBus.Fire(new TestCapSignal(42));

            Assert.AreEqual(1, state.Executions);
            Assert.AreEqual(42, state.LastAmount);
        }

        [Test]
        public void BindInterfacesAndSelfTo_Resolves_AllContractsAndConcreteType_ToSameSingleton()
        {
            var state = new TestCapState();
            using var ctx = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance(state);
                builder.BindInterfacesAndSelfTo<TestDomainController>();
            });

            var asContractA = ctx.Context.Resolve<ITestContractA>();
            var asContractB = ctx.Context.Resolve<ITestContractB>();
            var asSelf = ctx.Context.Resolve<TestDomainController>();

            Assert.IsNotNull(asContractA);
            Assert.AreSame(asContractA, asContractB);
            Assert.AreSame(asContractB, asSelf);
            Assert.AreEqual("DomainController", asContractA.Name);
            Assert.AreEqual(99, asContractB.Level);
        }

        [Test]
        public async Task FlexibleDomainLifecycles_Executes_Start_And_Stop_Hooks()
        {
            var state = new TestCapState();
            using var ctx = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance(state);
                builder.BindInterfacesAndSelfTo<TestDomainController>();
            });

            // Trigger resolution
            ctx.Context.Resolve<TestDomainController>();

            var orchestrator = new ContextLifecycleOrchestrator();
            var singletons = ctx.Context.Container.GetActiveSingletons();

            await orchestrator.ExecuteStartableLifecyclesAsync(singletons, CancellationToken.None);

            Assert.IsTrue(state.StartSyncCalled, "IStartable.Start must be invoked");
            Assert.IsTrue(state.StartAsyncCalled, "IAsyncStartable.StartAsync must be invoked");

            await orchestrator.ExecuteStoppableLifecyclesAsync(singletons, CancellationToken.None);

            Assert.IsTrue(state.StopSyncCalled, "IStoppable.Stop must be invoked");
            Assert.IsTrue(state.StopAsyncCalled, "IAsyncStoppable.StopAsync must be invoked");
        }

        [Test]
        public void NexusBinding_Component_Injects_MonoBehaviour_Target()
        {
            var state = new TestCapState();
            using var ctx = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance(state);
            });

            var go = new GameObject("TestBindingGO");
            try
            {
                var binding = go.AddComponent<NexusBinding>();
                var target = go.AddComponent<TestInjectedComponent>();

                binding.InjectNow(ctx.Context);

                Assert.IsNotNull(target.State, "Dependency must be injected into target component");
                Assert.AreSame(state, target.State);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GlobalRoot_RegistersAndAutoBindsToOrphanSceneRoot()
        {
            GameObject globalGo = null;
            GameObject sceneGo = null;
            ContextData globalData = null;
            ContextData sceneData = null;
            try
            {
                globalData = ScriptableObject.CreateInstance<ContextData>();
                globalData.ScopeTag = "Global";
                globalData.EnableAutoDiscovery = false;

                sceneData = ScriptableObject.CreateInstance<ContextData>();
                sceneData.ScopeTag = "Scene";
                sceneData.EnableAutoDiscovery = false;

                globalGo = new GameObject("[Global_Root]");
                globalGo.SetActive(false);
                var globalRoot = globalGo.AddComponent<Root>();
                globalRoot.SetUp(globalData, null, 0);
                globalRoot.IsGlobalContext = true;
                globalGo.SetActive(true);
                globalRoot.InitializeContext();

                Assert.AreEqual(globalRoot, NexusRuntime.GlobalRoot);
                Assert.IsNotNull(globalRoot.Context);
                Assert.AreEqual(globalRoot.Context, NexusRuntime.GlobalContext);

                sceneGo = new GameObject("[Scene_Root]");
                sceneGo.SetActive(false);
                var sceneRoot = sceneGo.AddComponent<Root>();
                sceneRoot.SetUp(sceneData, null, 0);
                sceneRoot.AutoBindGlobalParent = true;
                sceneGo.SetActive(true);
                sceneRoot.InitializeContext();

                Assert.AreEqual(globalRoot, sceneRoot.ParentRoot, "Scene root should auto-bind to NexusRuntime.GlobalRoot when parentRoot is null");
                Assert.AreEqual(globalRoot.Context, sceneRoot.Context.Parent, "Scene root context should have GlobalRoot context as its parent");
            }
            finally
            {
                if (sceneGo != null) Object.DestroyImmediate(sceneGo);
                if (globalGo != null) Object.DestroyImmediate(globalGo);
                if (globalData != null) Object.DestroyImmediate(globalData);
                if (sceneData != null) Object.DestroyImmediate(sceneData);
                NexusRuntime.UnregisterGlobalRoot(NexusRuntime.GlobalRoot);
            }
        }

        [Test]
        public void GlobalRoot_UnregistersWhenDestroyed()
        {
            GameObject globalGo = null;
            ContextData globalData = null;
            try
            {
                globalData = ScriptableObject.CreateInstance<ContextData>();
                globalData.ScopeTag = "Global";
                globalData.EnableAutoDiscovery = false;

                globalGo = new GameObject("[Global_Root]");
                globalGo.SetActive(false);
                var globalRoot = globalGo.AddComponent<Root>();
                globalRoot.SetUp(globalData, null, 0);
                globalRoot.IsGlobalContext = true;
                globalGo.SetActive(true);
                globalRoot.InitializeContext();

                Assert.AreEqual(globalRoot, NexusRuntime.GlobalRoot);

                Object.DestroyImmediate(globalGo);
                globalGo = null;
                Assert.IsNull(NexusRuntime.GlobalRoot, "GlobalRoot should be null after destruction");
            }
            finally
            {
                if (globalGo != null) Object.DestroyImmediate(globalGo);
                if (globalData != null) Object.DestroyImmediate(globalData);
                NexusRuntime.UnregisterGlobalRoot(NexusRuntime.GlobalRoot);
            }
        }

        [Test]
        public void NexusBehaviour_Resolves_Subscribes_And_AutoDisposesOnDestroy()
        {
            var state = new TestCapState();
            using var ctx = NexusTestHarness.CreateContext(builder =>
            {
                builder.BindInstance(state);
            });
            NexusRuntime.RegisterContext(ctx.Context);

            GameObject go = new GameObject("TestNexusBehaviourGO");
            try
            {
                var comp = go.AddComponent<TestPlayerBehaviour>();
                comp.Context = ctx.Context;
                comp.InitializeLifecycle();

                Assert.IsNotNull(comp.Context, "NexusBehaviour must acquire active context");

                // Injection test
                Assert.IsNotNull(comp.State, "[Inject] must populate State on NexusBehaviour");
                Assert.AreSame(state, comp.State);

                // Resolution test
                var resolvedState = comp.Resolve<TestCapState>();
                Assert.AreSame(state, resolvedState);

                // Signal fire and subscription test
                comp.Fire(new TestCapSignal(100));
                Assert.AreEqual(100, comp.ReceivedSignalAmount);

                // Destroy component and verify signal unsubscription
                comp.DestroyLifecycle();
                Object.DestroyImmediate(go);
                go = null;

                // Dispatch signal again - destroyed behaviour must not receive it
                ctx.Context.SignalBus.Fire(new TestCapSignal(200));
                Assert.AreEqual(100, comp.ReceivedSignalAmount, "Unsubscribed signal handler must not fire after destruction");
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                NexusRuntime.UnregisterContext(ctx.Context);
            }
        }
    }

    public class TestPlayerBehaviour : NexusBehaviour
    {
        [Inject] public TestCapState State { get; set; }
        public int ReceivedSignalAmount { get; private set; }
        public bool LifecycleAwakeCalled { get; private set; }
        public bool LifecycleDestroyCalled { get; private set; }

        protected override void OnNexusAwake()
        {
            LifecycleAwakeCalled = true;
            Subscribe<TestCapSignal>(sig =>
            {
                ReceivedSignalAmount = sig.Amount;
            });
        }

        protected override void OnNexusDestroy()
        {
            LifecycleDestroyCalled = true;
        }
    }
}
