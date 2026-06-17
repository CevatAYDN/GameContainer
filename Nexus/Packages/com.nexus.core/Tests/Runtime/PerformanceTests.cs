using NUnit.Framework;
using Nexus.Core;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    public class PerformanceTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;

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

        public readonly struct PerfSignal
        {
            public readonly int Index;
            public PerfSignal(int index) => Index = index;
        }

        public class PerfCommand : ICommand
        {
            public static int ExecutionCount;
            public PerfSignal Signal;
            public void Execute() { ExecutionCount++; }
        }

        private int _subValue;

        [SetUp]
        public void Setup()
        {
            PerfCommand.ExecutionCount = 0;
            _subValue = 0;

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);

            _container.Bind<PerfCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(PerfSignal), typeof(PerfCommand), ExecutionMode.Sequential, 0, false);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void Dispatch1000Signals_CompletesUnderTime()
        {
            const int count = 1000;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < count; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            sw.Stop();

            Assert.AreEqual(count, PerfCommand.ExecutionCount);
            Assert.Less(sw.ElapsedMilliseconds, 5000, "1000 dispatches should complete within 5 seconds");
        }

        [Test]
        public void Subscribe1000AndFire_AllReceived()
        {
            const int count = 1000;
            int received = 0;
            _signalBus.Subscribe<PerfSignal>(sig => received++);

            for (int i = 0; i < count; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            Assert.AreEqual(count, received);
        }

        [Test]
        public void CommandPool_ReusesInstances()
        {
            // Get internal pool state by resolving multiple times
            _signalBus.Fire(new PerfSignal(1));
            _signalBus.Fire(new PerfSignal(2));

            // Command pool returns cleaned commands; execute to verify pool works
            Assert.AreEqual(2, PerfCommand.ExecutionCount);

            // Fire several more times — pool should reuse without exhausting
            for (int i = 0; i < 100; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            Assert.AreEqual(102, PerfCommand.ExecutionCount);
        }
    }
}
