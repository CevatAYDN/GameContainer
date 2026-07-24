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

        public readonly struct ScoreSignal
        {
            public readonly int Score;
            public ScoreSignal(int score) { Score = score; }
        }

        public readonly struct ComboSignal
        {
            public readonly int Combo;
            public ComboSignal(int combo) { Combo = combo; }
        }

        public class PayloadCompositeCommand : ICompositeCommand
        {
            public static int ExecutionCount;
            public static bool HadScore;
            public static int CapturedScore;
            public static int CapturedCombo;
            public void Execute(CompositeContext signals)
            {
                ExecutionCount++;
                HadScore = signals.TryGet<ScoreSignal>(out var score);
                CapturedScore = score.Score;
                CapturedCombo = signals.Get<ComboSignal>().Combo;
            }
        }

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
            PayloadCompositeCommand.ExecutionCount = 0;
            PayloadCompositeCommand.HadScore = false;
            PayloadCompositeCommand.CapturedScore = 0;
            PayloadCompositeCommand.CapturedCombo = 0;
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
            TestCompositeCommand.ExecutionCount = 0;
            HighPriorityCompositeCommand.RunOrder = 0;
            HighPriorityCompositeCommand.ObservedOrder = 0;
            LowPriorityCompositeCommand.ObservedOrder = 0;
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

        [Test]
        public void CompositeCommand_ReceivesSignalPayloads()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(ScoreSignal), typeof(ComboSignal) },
                typeof(PayloadCompositeCommand),
                oneShot: true,
                priority: 0,
                isAsync: false
            );

            _signalBus.Fire(new ScoreSignal(42));
            _signalBus.Fire(new ComboSignal(7));

            Assert.AreEqual(1, PayloadCompositeCommand.ExecutionCount);
            Assert.IsTrue(PayloadCompositeCommand.HadScore, "ScoreSignal payload should be present in the composite context.");
            Assert.AreEqual(42, PayloadCompositeCommand.CapturedScore);
            Assert.AreEqual(7, PayloadCompositeCommand.CapturedCombo);
        }

        [Test]
        public void ReTriggerable_CapturesMostRecentPayloadPerCycle()
        {
            _signalBus.RegisterCompositeCommand(
                new[] { typeof(ScoreSignal), typeof(ComboSignal) },
                typeof(PayloadCompositeCommand),
                oneShot: false,
                priority: 0,
                isAsync: false
            );

            // First cycle.
            _signalBus.Fire(new ScoreSignal(1));
            _signalBus.Fire(new ComboSignal(1));
            Assert.AreEqual(1, PayloadCompositeCommand.ExecutionCount);
            Assert.AreEqual(1, PayloadCompositeCommand.CapturedScore);

            // Second cycle — payloads must reflect the latest fire, not stale values.
            _signalBus.Fire(new ScoreSignal(99));
            _signalBus.Fire(new ComboSignal(50));
            Assert.AreEqual(2, PayloadCompositeCommand.ExecutionCount);
            Assert.AreEqual(99, PayloadCompositeCommand.CapturedScore);
            Assert.AreEqual(50, PayloadCompositeCommand.CapturedCombo);
        }
    }
}
