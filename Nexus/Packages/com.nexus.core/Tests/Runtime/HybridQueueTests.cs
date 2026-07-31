using NUnit.Framework;
using Nexus.Core;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    [Ignore("bisect: temporarily excluded to isolate PlayMode hang poison")]
    public class HybridQueueTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private HybridQueue _queue;
        private MockContext _context;

        public readonly struct QueueTestSignal
        {
            public readonly int Id;
            public QueueTestSignal(int id) => Id = id;
        }

        public readonly struct OrderedSignalA { }
        public readonly struct OrderedSignalB { }

        private int _receivedValue;

        [SetUp]
        public void Setup()
        {
            _receivedValue = 0;

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _queue = new HybridQueue(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void FireThreadSafe_QueuesAndDrains()
        {
            _signalBus.Subscribe<QueueTestSignal>(sig => _receivedValue = sig.Id);

            _queue.EnqueueThreadSafe(new QueueTestSignal(42));

            // Signal not yet dispatched
            Assert.AreEqual(0, _receivedValue);

            // Drain thread-safe queue (simulates Update)
            _queue.DrainThreadSafe();

            Assert.AreEqual(42, _receivedValue);
        }

        [Test]
        public void FireNextFrame_QueuesAndDrains()
        {
            _signalBus.Subscribe<QueueTestSignal>(sig => _receivedValue = sig.Id);

            _queue.EnqueueNextFrame(new QueueTestSignal(99));

            // Signal not yet dispatched
            Assert.AreEqual(0, _receivedValue);

            // Drain next-frame queue (simulates LateUpdate)
            _queue.DrainNextFrame();

            Assert.AreEqual(99, _receivedValue);
        }

        [Test]
        public void ThreadSafeDrained_Before_NextFrame()
        {
            var order = new System.Collections.Generic.List<string>();
            _signalBus.Subscribe<OrderedSignalA>(sig => order.Add("A"));
            _signalBus.Subscribe<OrderedSignalB>(sig => order.Add("B"));

            // Enqueue to both queues
            _queue.EnqueueNextFrame(new OrderedSignalB());
            _queue.EnqueueThreadSafe(new OrderedSignalA());

            // Thread-safe queue drains first (simulates Update)
            _queue.DrainThreadSafe();
            Assert.AreEqual("A", order[0]);

            // Then next-frame queue (simulates LateUpdate)
            _queue.DrainNextFrame();
            Assert.AreEqual("B", order[1]);

            // Verify order: A before B
            Assert.AreEqual(2, order.Count);
        }

        [Test]
        public void MultipleThreadSafeSignals_AllDispatched()
        {
            int count = 0;
            _signalBus.Subscribe<QueueTestSignal>(sig => count++);

            _queue.EnqueueThreadSafe(new QueueTestSignal(1));
            _queue.EnqueueThreadSafe(new QueueTestSignal(2));
            _queue.EnqueueThreadSafe(new QueueTestSignal(3));

            _queue.DrainThreadSafe();

            Assert.AreEqual(3, count);
        }

        [Test]
        public void PreservesChronologicalInterleavedOrder()
        {
            var order = new System.Collections.Generic.List<string>();
            _signalBus.Subscribe<OrderedSignalA>(sig => order.Add("A"));
            _signalBus.Subscribe<OrderedSignalB>(sig => order.Add("B"));

            _queue.EnqueueThreadSafe(new OrderedSignalA());
            _queue.EnqueueThreadSafe(new OrderedSignalB());
            _queue.EnqueueThreadSafe(new OrderedSignalA());

            _queue.DrainThreadSafe();

            Assert.AreEqual(3, order.Count);
            Assert.AreEqual("A", order[0]);
            Assert.AreEqual("B", order[1]);
            Assert.AreEqual("A", order[2]);
        }
    }
}
