using NUnit.Framework;
using Nexus.Core;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    public class CompositeTriggerTests
    {
        public readonly struct SignalA { }
        public readonly struct SignalB { }
        public readonly struct SignalC { }

        public class TestCompositeCommand : ICommand
        {
            public static int ExecutionCount;
            public void Execute() { ExecutionCount++; }
        }

        public class HighPriorityCompositeCommand : ICommand
        {
            public static int RunOrder;
            public static int ObservedOrder;
            public void Execute() { ObservedOrder = ++RunOrder; }
        }

        public class LowPriorityCompositeCommand : ICommand
        {
            public static int ObservedOrder;
            public void Execute() { ObservedOrder = ++HighPriorityCompositeCommand.RunOrder; }
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;

        [SetUp]
        public void Setup()
        {
            TestCompositeCommand.ExecutionCount = 0;
            HighPriorityCompositeCommand.RunOrder = 0;
            HighPriorityCompositeCommand.ObservedOrder = 0;
            LowPriorityCompositeCommand.ObservedOrder = 0;
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void OneShot_TriggersOnce()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(SignalA), typeof(SignalB), typeof(SignalC) },
                typeof(TestCompositeCommand),
                oneShot: true,
                priority: 0,
                isAsync: false
            );

            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());
            _signalBus.Fire(new SignalC());

            Assert.AreEqual(1, TestCompositeCommand.ExecutionCount);

            // Fire again — oneShot=true should NOT re-trigger
            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());
            _signalBus.Fire(new SignalC());

            Assert.AreEqual(1, TestCompositeCommand.ExecutionCount);
        }

        [Test]
        public void ReTriggerable_TriggersOnEachCycle()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(SignalA), typeof(SignalB) },
                typeof(TestCompositeCommand),
                oneShot: false,
                priority: 0,
                isAsync: false
            );

            // First cycle
            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());
            Assert.AreEqual(1, TestCompositeCommand.ExecutionCount);

            // Second cycle — mask reset, should trigger again
            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());
            Assert.AreEqual(2, TestCompositeCommand.ExecutionCount);
        }

        [Test]
        public void Idempotent_DuplicateSignal_DoesNotDoubleTrigger()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(SignalA), typeof(SignalB) },
                typeof(TestCompositeCommand),
                oneShot: false,
                priority: 0,
                isAsync: false
            );

            // Send SignalA twice (idempotent)
            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());

            // Should trigger only once
            Assert.AreEqual(1, TestCompositeCommand.ExecutionCount);
        }

        [Test]
        public void CompositeTriggers_ExecuteInPriorityOrder()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(SignalA), typeof(SignalB) },
                typeof(LowPriorityCompositeCommand),
                oneShot: false,
                priority: 0,
                isAsync: false
            );
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(SignalA), typeof(SignalB) },
                typeof(HighPriorityCompositeCommand),
                oneShot: false,
                priority: 100,
                isAsync: false
            );

            _signalBus.Fire(new SignalA());
            _signalBus.Fire(new SignalB());

            Assert.AreEqual(1, HighPriorityCompositeCommand.ObservedOrder);
            Assert.AreEqual(2, LowPriorityCompositeCommand.ObservedOrder);
        }
    }
}
