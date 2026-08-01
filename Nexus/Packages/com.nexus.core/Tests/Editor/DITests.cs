using NUnit.Framework;
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class DITests
    {
        // ── test helpers ───────────────────────────────────────
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

        public class ConstructorInjectedClass
        {
            public IDependency Dependency { get; }

            [Inject]
            public ConstructorInjectedClass(IDependency dependency)
            {
                Dependency = dependency;
            }
        }

        public class ValueTypeInjectedClass
        {
            [Inject] public int InvalidValue;
        }

        [Test]
        public void BindAndResolve_ReturnsCorrectInstances()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();

            var instance = di.Resolve<IDependency>();

            Assert.IsNotNull(instance);
            Assert.IsInstanceOf<ConcreteDependency>(instance);
        }

        [Test]
        public void Inject_PopulatesFieldsPropertiesAndMethods()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<DependentClass>(isSingleton: false);

            var dependent = di.Resolve<DependentClass>();

            Assert.IsNotNull(dependent.InjectedField);
            Assert.IsNotNull(dependent.InjectedProperty);
            Assert.IsNotNull(dependent.MethodInjectedDependency);
            
            Assert.AreSame(dependent.InjectedField, dependent.InjectedProperty);
            Assert.AreSame(dependent.InjectedField, dependent.MethodInjectedDependency);
        }

        [Test]
        public void HierarchicalResolve_ResolvesFromParent()
        {
            using var parentDi = new NexusDI();
            parentDi.Bind<IDependency, ConcreteDependency>();

            using var childDi = new NexusDI(parentDi);
            var resolved = childDi.Resolve<IDependency>();

            Assert.IsNotNull(resolved);
            Assert.IsInstanceOf<ConcreteDependency>(resolved);
        }

        [Test]
        public void InjectConstructorAttribute_ResolvesConstructorDependencies()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<ConstructorInjectedClass>(isSingleton: false);

            var resolved = di.Resolve<ConstructorInjectedClass>();

            Assert.IsNotNull(resolved.Dependency);
            Assert.IsInstanceOf<ConcreteDependency>(resolved.Dependency);
        }

        [Test]
        public void InjectValueTypeField_ThrowsExplicitError()
        {
            using var di = new NexusDI();
            di.Bind<ValueTypeInjectedClass>(isSingleton: false);

            Assert.Throws<System.InvalidOperationException>(() => di.Resolve<ValueTypeInjectedClass>());
        }

        public class DisposableSingleton : System.IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        [Test]
        public void BindInstance_Disposable_DisposedWithContainer()
        {
            var instance = new DisposableSingleton();
            using var di = new NexusDI();
            di.BindInstance(instance, disposeWithContainer: true);

            Assert.IsFalse(instance.Disposed);

            di.Dispose();

            Assert.IsTrue(instance.Disposed);
        }

        public class ServiceA
        {
            public ServiceA(ServiceB serviceB) { }
        }

        public class ServiceB
        {
            public ServiceB(ServiceA serviceA) { }
        }

        [Test]
        public void Resolve_CircularDependency_ThrowsException()
        {
            using var di = new NexusDI();
            di.Bind<ServiceA>(isSingleton: true);
            di.Bind<ServiceB>(isSingleton: true);

            Assert.Throws<System.InvalidOperationException>(() => di.Resolve<ServiceA>());
        }

        public class ResettableCommand : Nexus.Core.IResettable
        {
            [Inject] public IDependency DependencyField;
            public bool ResetCalled;

            public void Reset()
            {
                ResetCalled = true;
                DependencyField = null;
            }
        }

        [Test]
        public void ClearInjectedReferences_Resettable_CallsReset()
        {
            using var di = new NexusDI();
            di.Bind<IDependency, ConcreteDependency>();
            di.Bind<ResettableCommand>(isSingleton: false);

            var cmd = di.Resolve<ResettableCommand>();
            Assert.IsNotNull(cmd.DependencyField);

            Nexus.Core.NexusDI.ClearInjectedReferences(cmd);

            Assert.IsNull(cmd.DependencyField);
            Assert.IsTrue(cmd.ResetCalled);
        }

        // ── ContextBuilder fluent API smoke test ──────────────

        public struct TestSignal { public int Value; }
        public class TestCommand : ICommand<TestSignal>
        {
            public static int LastValue;
            public void Execute(TestSignal signal) => LastValue = signal.Value;
        }
        public class TestReactiveModel : IReactiveModel
        {
            public int BindCount;
            public ValueTask OnBind(CancellationToken ct) { BindCount++; return default; }
        }

        [Test]
        public void ContextBuilder_BindSignalTo_And_BindReactiveModel()
        {
            TestCommand.LastValue = 0;
            using var di = new NexusDI();
            var poolManager = new CommandPoolManager(di, 4, 64);
            var bus = new SignalBus(di, poolManager, null);
            var builder = new ContextBuilder(di, bus);

            builder.BindSignal<TestSignal>().To<TestCommand>();
            builder.BindReactiveModel<TestReactiveModel>();
            di.Bind<TestReactiveModel>(isSingleton: true);

            bus.Fire(new TestSignal { Value = 42 });
            Assert.AreEqual(42, TestCommand.LastValue,
                "BindSignal<TSignal>().To<TCommand>() must register the command.");
        }
    }
}
