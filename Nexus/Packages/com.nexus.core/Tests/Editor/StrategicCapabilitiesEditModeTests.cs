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
            var binding = go.AddComponent<NexusBinding>();
            var target = go.AddComponent<TestInjectedComponent>();

            binding.InjectNow();

            Assert.IsNotNull(target.State, "Dependency must be injected into target component");
            Assert.AreSame(state, target.State);

            Object.DestroyImmediate(go);
        }
    }
}
