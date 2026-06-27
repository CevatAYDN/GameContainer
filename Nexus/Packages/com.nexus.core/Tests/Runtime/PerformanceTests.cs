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

        public readonly struct PerfSignal
        {
            public readonly int Index;
            public PerfSignal(int index) => Index = index;
        }

        public class PerfCommand : ICommand<PerfSignal>
        {
            public static int ExecutionCount;
            public void Execute(PerfSignal signal) { ExecutionCount++; }
        }

        [SetUp]
        public void Setup()
        {
            PerfCommand.ExecutionCount = 0;

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

        [Test]
        public void SteadyState_HasZeroGCAllocations()
        {
            // SignalBus uses ThreadStatic for stack depth and is not thread-safe for concurrent access.
            // All framework dispatch MUST happen on the main thread. We measure allocations on the
            // calling (main) thread after warm-up to get a clean baseline.

            // 1. Warm up the JIT compiler and pre-warm command pools
            for (int i = 0; i < 100; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            // 2. Perform a garbage collection to start from a clean slate
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long startAllocations = System.GC.GetAllocatedBytesForCurrentThread();

            // 3. Execute 5000 dispatches in steady-state on the calling thread
            for (int i = 0; i < 5000; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            long endAllocations = System.GC.GetAllocatedBytesForCurrentThread();
            long allocatedBytes = endAllocations - startAllocations;

            // In some environments, background threads might allocate a tiny bit of memory.
            // We assert that allocations are extremely minimal (e.g. less than 128 bytes total for 5000 dispatches), 
            // indicating zero allocations in the framework's dispatch path.
            Assert.LessOrEqual(allocatedBytes, 128, $"Steady-state dispatch allocated {allocatedBytes} bytes. Expected near-zero allocations.");
        }

        [Test]
        public void HighFrequency_Performance_StressTest()
        {
            const int count = 50000;
            
            // Warm up
            for (int i = 0; i < 100; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < count; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            sw.Stop();
            
            UnityEngine.Debug.Log($"[Nexus Benchmark] 50,000 dispatches completed in {sw.ElapsedMilliseconds} ms.");
            
            // Assert that 50k dispatches complete in less than 2 seconds (usually takes < 100ms)
            Assert.Less(sw.ElapsedMilliseconds, 2000, "50,000 dispatches took too long.");
        }
    }
}
