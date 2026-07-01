using NUnit.Framework;
using Nexus.Core;
using Nexus.Netcode;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    public class NetcodeTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;
        private NetworkSignalBus _networkBus;

        public readonly struct TestPlayerSignal : INetworkSignal
        {
            public readonly int PlayerId;
            public readonly int Health;
            public TestPlayerSignal(int playerId, int health)
            {
                PlayerId = playerId;
                Health = health;
            }
        }

        public class TestPlayerSnapshot : ISnapshotableModel<TestPlayerSignal>
        {
            public int LastHealth;
            public TestPlayerSignal CaptureSnapshot() => new TestPlayerSignal(0, LastHealth);
            public void RestoreSnapshot(TestPlayerSignal state) => LastHealth = state.Health;
        }

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _networkBus = new NetworkSignalBus(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void Fire_StoresTypedHistory_WithoutBoxing()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(1, history.Signals.Count);
            Assert.AreEqual(100, history.Signals[0].Signal.Health);
            Assert.AreEqual(0, history.Signals[0].Tick);
        }

        [Test]
        public void RollbackAndResimulate_ReplaysTypedSignals_NoReflection()
        {
            var model = new TestPlayerSnapshot();
            _networkBus.RegisterModel(model);

            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(1, 80));

            _networkBus.RollbackAndResimulate(rollbackTick: 0, targetTick: 1);

            Assert.AreEqual(80, model.LastHealth);
        }

        [Test]
        public void PruneHistory_RemovesOldEntries()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(1, 90));
            _networkBus.SetTick(2);
            _networkBus.Fire(new TestPlayerSignal(1, 80));

            _networkBus.PruneHistory(confirmedTick: 1);

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(1, history.Signals.Count);
            Assert.AreEqual(80, history.Signals[0].Signal.Health);
        }

        [Test]
        public void Clear_RemovesAllHistoriesAndModels()
        {
            var model = new TestPlayerSnapshot();
            _networkBus.RegisterModel(model);
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));

            _networkBus.Clear();

            Assert.AreEqual(0, _networkBus.Histories.Count);
        }
    }
}
