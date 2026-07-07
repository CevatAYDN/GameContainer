using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using System.Threading.Tasks;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class QueueTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private MockContext _context;
        private SignalBus _signalBus;
        private HybridQueue _hybridQueue;

        private int _receivedCount;
        private int _lastValue;

        public struct SimpleSignal
        {
            public int Value;
            public SimpleSignal(int value) => Value = value;
        }

        [SetUp]
        public void Setup()
        {
            _receivedCount = 0;
            _lastValue = 0;

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _hybridQueue = new HybridQueue(_signalBus);

            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
            _container.BindInstance(_hybridQueue);

            _signalBus.Subscribe<SimpleSignal>(sig =>
            {
                _receivedCount++;
                _lastValue = sig.Value;
            });
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void ThreadSafeQueue_DoesNotFireImmediately_FiresOnDrain()
        {
            _signalBus.FireThreadSafe(new SimpleSignal(7));

            Assert.AreEqual(0, _receivedCount);

            _hybridQueue.DrainThreadSafe();

            Assert.AreEqual(1, _receivedCount);
            Assert.AreEqual(7, _lastValue);
        }

        [Test]
        public async Task ThreadSafeQueue_WorksFromOtherThread()
        {
            await Task.Run(() =>
            {
                _signalBus.FireThreadSafe(new SimpleSignal(88));
            });

            Assert.AreEqual(0, _receivedCount);

            _hybridQueue.DrainThreadSafe();

            Assert.AreEqual(1, _receivedCount);
            Assert.AreEqual(88, _lastValue);
        }

        [Test]
        public void NextFrameQueue_FiresOnDrainNextFrame()
        {
            _signalBus.FireNextFrame(new SimpleSignal(55));

            Assert.AreEqual(0, _receivedCount);

            _hybridQueue.DrainThreadSafe();
            Assert.AreEqual(0, _receivedCount);

            _hybridQueue.DrainNextFrame();

            Assert.AreEqual(1, _receivedCount);
            Assert.AreEqual(55, _lastValue);
        }
    }
}
