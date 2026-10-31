using NUnit.Framework;
using Nexus.Core;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    [TestFixture]
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
        public void FireNextFrame_ReentrantEnqueue_WaitsForFollowingDrain()
        {
            var received = new System.Collections.Generic.List<int>();
            _signalBus.Subscribe<QueueTestSignal>(sig =>
            {
                received.Add(sig.Id);
                if (sig.Id == 1)
                    _queue.EnqueueNextFrame(new QueueTestSignal(2));
            });

            _queue.EnqueueNextFrame(new QueueTestSignal(1));
            _queue.DrainNextFrame();

            CollectionAssert.AreEqual(new[] { 1 }, received,
                "A signal enqueued by a next-frame handler belongs to the following drain generation.");
            Assert.AreEqual(1, _queue.NextFrameQueueDepth);

            _queue.DrainNextFrame();

            CollectionAssert.AreEqual(new[] { 1, 2 }, received);
            Assert.AreEqual(0, _queue.NextFrameQueueDepth);
        }

        [Test]
        public void FireThreadSafe_ReentrantEnqueue_WaitsForFollowingDrain()
        {
            var received = new List<int>();
            _signalBus.Subscribe<QueueTestSignal>(sig =>
            {
                received.Add(sig.Id);
                // re-fires itself up to 50 so a drain that runs until empty (issue #169) fails here instead of hanging
                if (sig.Id < 50)
                    _queue.EnqueueThreadSafe(new QueueTestSignal(sig.Id + 1));
            });

            _queue.EnqueueThreadSafe(new QueueTestSignal(1));
            _queue.DrainThreadSafe();

            CollectionAssert.AreEqual(new[] { 1 }, received,
                "A signal a thread-safe handler enqueues belongs to the following drain.");
            Assert.AreEqual(1, _queue.ThreadSafeQueueDepth);

            _queue.DrainThreadSafe();

            CollectionAssert.AreEqual(new[] { 1, 2 }, received);
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

        [UnityTest]
        public IEnumerator ContextWithoutRoot_RunsFireThreadSafeAndFireNextFrame()
        {
            var boot = ContextFactory.StartAsync("QueueWithoutRoot", _ => { });
            while (!boot.IsCompleted) yield return null;
            var context = boot.GetAwaiter().GetResult();
            try
            {
                var received = new List<int>();
                context.SignalBus.Subscribe<QueueTestSignal>(sig => received.Add(sig.Id));

                context.SignalBus.FireThreadSafe(new QueueTestSignal(1));
                context.SignalBus.FireNextFrame(new QueueTestSignal(2));
                yield return null;
                yield return null;

                CollectionAssert.AreEquivalent(new[] { 1, 2 }, received,
                    "A context without a Root drains its queues every frame (issue #169).");
            }
            finally { context.Dispose(); }
        }

        [UnityTest]
        public IEnumerator RootContext_IsDrainedOncePerFrame()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var go = new GameObject("Queue root");
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            root.SetUp(data);
            go.SetActive(true);
            try
            {
                for (int i = 0; i < 10 && !root.IsInitialized; i++) yield return null;
                Assert.IsTrue(root.IsInitialized);
                var frames = new List<int>();
                root.Context.SignalBus.Subscribe<QueueTestSignal>(sig =>
                {
                    frames.Add(Time.frameCount);
                    if (sig.Id == 1) root.Context.SignalBus.FireNextFrame(new QueueTestSignal(2));
                });

                root.Context.SignalBus.FireNextFrame(new QueueTestSignal(1));
                yield return null;
                yield return null;
                yield return null;

                Assert.AreEqual(2, frames.Count);
                Assert.Less(frames[0], frames[1],
                    "Only the Root's QueueDrainer drains its context, so a re-fired next-frame signal runs on a later frame.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(data);
            }
        }
    }
}
