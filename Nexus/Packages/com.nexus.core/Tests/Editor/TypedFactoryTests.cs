using Nexus.Core;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    public sealed class FactoryDependency { }

    public sealed class FactoryProduct
    {
        public readonly FactoryDependency FromConstructor;
        public readonly int Level;
        public FactoryProduct(FactoryDependency fromConstructor, int level) { FromConstructor = fromConstructor; Level = level; }
        [Inject] public FactoryDependency FromMember { get; set; }
        public int PostConstructCalls;
        [PostConstruct] public void Ready() => PostConstructCalls++;
    }

    public sealed class ParameterlessProduct
    {
        public readonly FactoryDependency Dependency;
        public ParameterlessProduct(FactoryDependency dependency) => Dependency = dependency;
    }

    public sealed class FactoryConsumer
    {
        [Inject] public IFactory<int, FactoryProduct> Products { get; set; }
    }

    /// <summary>
    /// A typed factory lets a class create instances on demand without taking the container as a dependency.
    /// The container-backed factory must construct through DI (constructor, members, [PostConstruct]) and hand
    /// the runtime argument to the matching constructor parameter.
    /// </summary>
    public sealed class TypedFactoryTests
    {
        [Test]
        public void ContainerFactoryConstructsAndInjectsANewInstancePerCall()
        {
            using var di = new NexusDI();
            di.Bind<FactoryDependency>();
            di.BindTypedFactory<ParameterlessProduct>();

            var factory = di.Resolve<IFactory<ParameterlessProduct>>();
            var first = factory.Create();
            var second = factory.Create();

            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.Dependency, Is.SameAs(second.Dependency), "the dependency is the container's singleton");
        }

        [Test]
        public void ParameterFactoryPassesTheArgumentAndResolvesTheRest()
        {
            using var di = new NexusDI();
            di.Bind<FactoryDependency>();
            di.BindTypedFactory<int, FactoryProduct>();

            var product = di.Resolve<IFactory<int, FactoryProduct>>().Create(7);

            Assert.That(product.Level, Is.EqualTo(7));
            Assert.That(product.FromConstructor, Is.Not.Null);
            Assert.That(product.FromMember, Is.Not.Null);
            Assert.That(product.PostConstructCalls, Is.EqualTo(1));
        }

        [Test]
        public void FactoryIsAnOrdinaryInjectableDependency()
        {
            using var di = new NexusDI();
            di.Bind<FactoryDependency>();
            di.BindTypedFactory<int, FactoryProduct>();
            di.Bind<FactoryConsumer>();

            var consumer = di.Resolve<FactoryConsumer>();

            Assert.That(consumer.Products.Create(3).Level, Is.EqualTo(3));
        }

        [Test]
        public void DelegateFactoryReturnsWhatTheDelegateCreates()
        {
            using var di = new NexusDI();
            var dependency = new FactoryDependency();
            di.BindTypedFactory<int, FactoryProduct>(level => new FactoryProduct(dependency, level * 2));

            var product = di.Resolve<IFactory<int, FactoryProduct>>().Create(4);

            Assert.That(product.Level, Is.EqualTo(8));
            Assert.That(product.FromMember, Is.Null, "a delegate factory does not inject; it returns the delegate's object");
        }

        [Test]
        public void ConstructBuildsAnUnboundTypeThroughTheContainer()
        {
            using var di = new NexusDI();
            di.Bind<FactoryDependency>();

            var product = di.Construct<ParameterlessProduct>();

            Assert.That(product.Dependency, Is.Not.Null);
            Assert.That(di.IsRegistered(typeof(ParameterlessProduct)), Is.False, "Construct does not register the type");
        }
    }
}
