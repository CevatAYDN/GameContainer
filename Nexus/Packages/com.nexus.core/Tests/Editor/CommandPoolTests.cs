using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class CommandPoolTests
    {
        private NexusDI _container;
        private CommandPool _pool;

        public class MockDependency
        {
        }

        public class ResettableCommand : ICommand, IResettable
        {
            [Inject] public MockDependency Dependency;
            public bool WasReset { get; private set; }

            public void Execute() { }

            public void Reset()
            {
                WasReset = true;
            }
        }

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _container.Bind<MockDependency>();
            _container.Bind<ResettableCommand>(isSingleton: false);
        }

        [TearDown]
        public void TearDown()
        {
            _container.Dispose();
        }

        [Test]
        public void Get_ReturnsInjectedInstance()
        {
            _pool = new CommandPool(typeof(ResettableCommand), () => _container.Resolve(typeof(ResettableCommand)));

            var command = (ResettableCommand)_pool.Get();
            _container.Inject(command);

            Assert.IsNotNull(command);
            Assert.IsNotNull(command.Dependency);
            
            _pool.Return(command);
        }

        [Test]
        public void Return_CallsResetAndNullifiesDependencies()
        {
            _pool = new CommandPool(typeof(ResettableCommand), () => _container.Resolve(typeof(ResettableCommand)));

            var command = (ResettableCommand)_pool.Get();
            _container.Inject(command);

            Assert.IsNotNull(command.Dependency);
            Assert.IsFalse(command.WasReset);

            // Return to pool
            _pool.Return(command);

            Assert.IsTrue(command.WasReset);
            Assert.IsNull(command.Dependency);
        }
    }
}
