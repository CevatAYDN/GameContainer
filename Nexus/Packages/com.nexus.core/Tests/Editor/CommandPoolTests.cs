using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using UnityEngine;
using UnityEngine.TestTools;

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

        [Test]
        public void Return_DoubleReturn_DoesNotPoolTheSameInstanceTwice()
        {
            _pool = new CommandPool(typeof(ResettableCommand), () => _container.Resolve(typeof(ResettableCommand)));

            var command = (ResettableCommand)_pool.Get();
            _pool.Return(command);

            // Double-return: the second return must be discarded, not pushed again —
            // otherwise Get() would hand the same instance to two consumers.
            _pool.Return(command);

            var first = (ResettableCommand)_pool.Get();
            var second = (ResettableCommand)_pool.Get();

            Assert.AreSame(command, first);
            Assert.AreNotSame(first, second, "Pool must not contain the same instance twice.");
            Assert.GreaterOrEqual(_pool.GetStats().TotalDiscarded, 1);
        }

        public class InjectedPropertiesCommand : ICommand
        {
            [Inject] public MockDependency Dependency { get; set; }
            [Inject] private MockDependency Hidden { get; set; }
            public void Execute() { }
        }

        public class StatefulPropertyCommand : ICommand
        {
            [Inject] public MockDependency Dependency { get; set; }
            public MockDependency Leftover { get; set; }
            public void Execute() { }
        }

        // #80: the warning listed "<Dependency>k__BackingField" because [Inject] sits on the property, not the field.
        [Test]
        public void StateLeakWarning_IgnoresInjectAutoProperties()
        {
            CommandPool.ClearStateLeakWarningsStatic();
            _pool = new CommandPool(typeof(InjectedPropertiesCommand), () => new InjectedPropertiesCommand());
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void StateLeakWarning_StillNamesAPlainAutoPropertyNextToAnInjectedOne()
        {
            CommandPool.ClearStateLeakWarningsStatic();
            LogAssert.Expect(LogType.Warning, new Regex(@"StatefulPropertyCommand' has mutable field\(s\) \[<Leftover>k__BackingField\] "));
            _pool = new CommandPool(typeof(StatefulPropertyCommand), () => new StatefulPropertyCommand());
        }
    }
}
