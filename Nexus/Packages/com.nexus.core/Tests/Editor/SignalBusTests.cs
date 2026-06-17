using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class SignalBusTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private MockContext _context;
        private SignalBus _signalBus;

        public struct SimpleSignal
        {
            public int Value;
            public SimpleSignal(int value) => Value = value;
        }

        public class MockContext : IContext
        {
            public ISignalBus SignalBus => null;
            public CancellationToken LifetimeToken => CancellationToken.None;
            public IContext Parent => null;
            public void RegisterView(IView view) { }
            public void UnregisterView(IView view) { }
            public T Resolve<T>() where T : class => null;
            public void RegisterPlugin(INexusPlugin plugin) { }
            public void RemovePlugin(INexusPlugin plugin) { }
            public void Dispose() { }
        }

        public static int ExecutedCount = 0;
        public static int LastExecutedValue = 0;
        public static int PriorityRunOrder = 0;
        public static int FirstExecutedPriority = 0;
        public static int SecondExecutedPriority = 0;

        public class TestCommand : ICommand
        {
            public SimpleSignal Signal;
            
            public void Execute()
            {
                ExecutedCount++;
                LastExecutedValue = Signal.Value;
            }
        }

        public class HighPriorityCommand : ICommand
        {
            public void Execute()
            {
                PriorityRunOrder++;
                FirstExecutedPriority = PriorityRunOrder;
            }
        }

        public class LowPriorityCommand : ICommand
        {
            public void Execute()
            {
                PriorityRunOrder++;
                SecondExecutedPriority = PriorityRunOrder;
            }
        }

        public class ReentrantCommand : ICommand
        {
            [Inject] private ISignalBus _signalBus;

            public void Execute()
            {
                _signalBus.Fire(new SimpleSignal(10));
            }
        }

        [SetUp]
        public void Setup()
        {
            ExecutedCount = 0;
            LastExecutedValue = 0;
            PriorityRunOrder = 0;
            FirstExecutedPriority = 0;
            SecondExecutedPriority = 0;

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);

            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void Fire_ExecutesRegisteredCommandAndInjectsSignal()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 0, isAsync: false);

            _signalBus.Fire(new SimpleSignal(42));

            Assert.AreEqual(1, ExecutedCount);
            Assert.AreEqual(42, LastExecutedValue);
        }

        [Test]
        public void SequentialMode_ExecutesInPriorityOrder()
        {
            _container.Bind<HighPriorityCommand>(isSingleton: false);
            _container.Bind<LowPriorityCommand>(isSingleton: false);

            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(LowPriorityCommand), ExecutionMode.Sequential, 10, isAsync: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(HighPriorityCommand), ExecutionMode.Sequential, 100, isAsync: false);

            _signalBus.Fire(new SimpleSignal(5));

            Assert.AreEqual(1, FirstExecutedPriority);
            Assert.AreEqual(2, SecondExecutedPriority);
        }

        [Test]
        public void RegisterCommand_MixedModes_ThrowsException()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 0, isAsync: false);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Concurrent, 0, isAsync: false);
            });
        }

        [Test]
        public void RegisterCommand_DuplicatePriority_ThrowsException()
        {
            _container.Bind<TestCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 10, isAsync: false);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(TestCommand), ExecutionMode.Sequential, 10, isAsync: false);
            });
        }

        [Test]
        public void ReentrancyProtection_StackOverflow_ThrowsNexusReentrancyException()
        {
            _container.Bind<ReentrantCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(SimpleSignal), typeof(ReentrantCommand), ExecutionMode.Sequential, 0, isAsync: false);

            Assert.Throws<NexusReentrancyException>(() =>
            {
                _signalBus.Fire(new SimpleSignal(1));
            });
        }

        [Test]
        public void Subscribe_InvokesHandlerOnFire()
        {
            int subValue = 0;
            _signalBus.Subscribe<SimpleSignal>(sig => subValue = sig.Value);

            _signalBus.Fire(new SimpleSignal(99));

            Assert.AreEqual(99, subValue);
        }
    }
}
