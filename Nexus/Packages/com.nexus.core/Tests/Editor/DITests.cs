using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class DITests
    {
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
    }
}
