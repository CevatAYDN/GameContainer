using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Netcode;
using NUnit.Framework;

namespace Nexus.Tests
{
    [TestFixture]
    public class NetworkSignalBusConcurrencyStressTests
    {
        public readonly struct NetSignal : INetworkSignal
        {
            public readonly int Value;
            public NetSignal(int v) { Value = v; }
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;
        private NetworkSignalBus _networkBus;
        private HybridQueue _queue;

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _queue = new HybridQueue(_signalBus);
            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
            _container.BindInstance(_queue);
            _networkBus = new NetworkSignalBus(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            try { _networkBus?.Clear(); } catch { }
            try { _queue?.Clear(); } catch { }
            try { _signalBus?.Dispose(); } catch { }
            try { _poolManager?.Clear(); } catch { }
            try { _container?.Dispose(); } catch { }
        }

        [Test]
        public async Task NetworkSignalBus_AllowsConcurrentFire_CreatesSingleHistoryAndRecordsAll()
        {
            const int parallel = 20;
            var tasks = new List<Task>(parallel);

            for (int i = 0; i < parallel; i++)
            {
                int v = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        _networkBus.Fire(new NetSignal(v));
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail("NetworkSignalBus.Fire threw: " + ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Verify a history exists for NetSignal type and contains expected entries
            var histories = _networkBus.Histories;
            Assert.IsTrue(histories.ContainsKey(typeof(NetSignal)), "History for NetSignal should exist");
            var hist = histories[typeof(NetSignal)] as NetworkSignalHistory<NetSignal>;
            Assert.IsNotNull(hist, "History cast should succeed");
            Assert.AreEqual(parallel, hist.Signals.Count, "All fired signals should be recorded in history");
        }

        [Test]
        public void NetworkSignalHistory_ParallelAddsRemainLossless()
        {
            var history = new NetworkSignalHistory<NetSignal>();
            const int total = 2000;
            Parallel.For(0, total, i => history.Add(i, new NetSignal(i)));

            var snapshot = history.Signals;
            Assert.AreEqual(total, snapshot.Count);
            Assert.AreEqual(total, snapshot.Select(x => x.Signal.Value).Distinct().Count());
        }

        [Test]
        public void NetworkSignalBus_FireFromWorker_MarshalsToOwningQueue()
        {
            int callbackThread = -1;
            int callbackCount = 0;
            _signalBus.Subscribe<NetSignal>(_ =>
            {
                callbackThread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref callbackCount);
            });

            int ownerThread = Thread.CurrentThread.ManagedThreadId;
            Task.Run(() => _networkBus.Fire(new NetSignal(7))).GetAwaiter().GetResult();

            Assert.AreEqual(0, Volatile.Read(ref callbackCount),
                "Worker Fire must not execute handlers before the main-thread drain.");

            _queue.DrainThreadSafe();

            Assert.AreEqual(1, Volatile.Read(ref callbackCount));
            Assert.AreEqual(ownerThread, callbackThread,
                "Network Fire handlers must execute on the queue owner thread.");
        }
    }
}
