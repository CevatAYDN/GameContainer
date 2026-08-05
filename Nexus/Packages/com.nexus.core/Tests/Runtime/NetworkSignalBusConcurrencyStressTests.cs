using System;
using System.Collections.Generic;
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

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
            _networkBus = new NetworkSignalBus(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            try { _networkBus?.Clear(); } catch { }
            try { _signalBus?.Dispose(); } catch { }
            try { _poolManager?.Clear(); } catch { }
            try { _container?.Dispose(); } catch { }
        }

        [Test]
        public async Task NetworkSignalBus_AllowsConcurrentFire_CreatesSingleHistoryAndRecordsAll()
        {
            const int parallel = 100;
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
    }
}
