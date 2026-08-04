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

        public readonly struct OtherSignal
        {
            public readonly int Value;
            public OtherSignal(int value) => Value = value;
        }

        public class TestCounter { public int Value; }

        public class PerfCommand : ICommand<PerfSignal>
        {
            [Inject] public TestCounter Counter;
            public void Execute(PerfSignal signal) { Counter.Value++; }
        }

        // A single command class that handles TWO different signal types (multi-signal
        // parameter support on one command via multiple generic command interfaces).
        public class MultiSignalCommand : ICommand<PerfSignal>, ICommand<OtherSignal>
        {
            [Inject] public TestCounter Counter;
            public void Execute(PerfSignal signal) { Counter.Value += signal.Index; }
            public void Execute(OtherSignal signal) { Counter.Value += signal.Value; }
        }

        private TestCounter _counter;

        [SetUp]
        public void Setup()
        {
            _counter = new TestCounter();
            _container = new NexusDI();
            _container.BindInstance(_counter);
            _container.Bind<PerfCommand>(isSingleton: false);
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);

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

            Assert.AreEqual(count, _counter.Value);
            Assert.Less(sw.ElapsedMilliseconds, 50, "1000 dispatches should complete within 50ms");
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
            Assert.AreEqual(2, _counter.Value);

            // Fire several more times — pool should reuse without exhausting
            for (int i = 0; i < 100; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            Assert.AreEqual(102, _counter.Value);
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

            // REVIEW FIX: the per-thread API only measures the calling thread, so background
            // thread allocations (async path / thread-pool continuations) are not captured.
            // Unity's test assembly does not expose GC.GetTotalAllocatedBytes() (not in .NET
            // Standard 2.0) nor Profiler.GetTotalAllocatedBytes(), so we keep the per-thread
            // counter but tighten the assertion to exactly 0 bytes. The .NET benchmark harness
            // (tools/nexus-benchmark) uses GC.GetTotalAllocatedBytes() for whole-process
            // coverage — see Program.SteadyState_HasZeroGCAllocations.
            long startAllocations = System.GC.GetAllocatedBytesForCurrentThread();

            // 3. Execute 5000 dispatches in steady-state on the calling thread
            for (int i = 0; i < 5000; i++)
            {
                _signalBus.Fire(new PerfSignal(i));
            }

            long endAllocations = System.GC.GetAllocatedBytesForCurrentThread();
            long allocatedBytes = endAllocations - startAllocations;

            // REVIEW FIX: tightened the limit from 128 bytes to 0 bytes. The framework's
            // "0 GC allocation" claim means the steady-state dispatch path must allocate
            // NOTHING. 128 bytes over 5000 dispatches (1 byte per 39 dispatches) was too
            // lenient and could mask a small per-dispatch allocation. With the warm-up +
            // GC.Collect() sequence, the JIT and pool growth are already paid; any remaining
            // allocation is a genuine framework allocation.
            Assert.AreEqual(0, allocatedBytes, $"Steady-state dispatch allocated {allocatedBytes} bytes. Expected zero allocations.");
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
            
            // Assert that 50k dispatches complete within a realistic test budget.
            Assert.Less(sw.ElapsedMilliseconds, 800, "50,000 dispatches took too long.");
        }

        [Test]
        public void Benchmark_SignalFire_HotPathNs()
        {
            const int warmup = 2000;
            const int iterations = 20000;

            // Warm up JIT + pre-warm command pool so we measure steady-state latency.
            for (int i = 0; i < warmup; i++)
                _signalBus.Fire(new PerfSignal(i));

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            double nsPerDispatch = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            UnityEngine.Debug.Log($"[Nexus Benchmark] SignalBus.Fire hot-path: {nsPerDispatch:F2} ns/dispatch over {iterations} dispatches (total {sw.ElapsedMilliseconds} ms)");

            // Regression guard for the published hot-path latency (P2-C). Editor/Mono measured ~9500ns;
            // IL2CPP/Release is faster, so 25us leaves ample headroom for CI variance.
            Assert.Less(nsPerDispatch, 25000, "Hot-path dispatch should stay well under 25us.");
        }

        [Test]
        public void SameCommandClass_RegisteredForMultipleSignals_ExecutesBoth()
        {
            // Regression: a command class implementing ICommand<PerfSignal> AND
            // ICommand<OtherSignal> must be registerable for both signal types and
            // execute for each — the pool is shared per command type.
            //
            // Uses an isolated bus: the Setup-registered PerfCommand also fires on
            // PerfSignal (+1), which would otherwise contaminate the expected total.
            var counter = new TestCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<MultiSignalCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());

            try
            {
                bus.RegisterCommand(typeof(PerfSignal), typeof(MultiSignalCommand), ExecutionMode.Sequential, 10, false);
                bus.RegisterCommand(typeof(OtherSignal), typeof(MultiSignalCommand), ExecutionMode.Sequential, 10, false);

                bus.Fire(new PerfSignal(3));
                bus.Fire(new OtherSignal(5));

                Assert.AreEqual(8, counter.Value,
                    "One command class must receive and execute both signal types (3 + 5).");
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        [Test]
        public void CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate()
        {
            // Regression: CommandPoolManager.GetCommand used to allocate a new closure
            // object on EVERY call (the GetOrAdd valueFactory captured `this`), i.e. one
            // heap allocation per command execution per signal fire. The static factory
            // overload keeps the hot path allocation-free.
            var mgr = new CommandPoolManager(_container);
            var cmdType = typeof(PerfCommand);

            // Warm up: JIT + pool growth + HashSet capacity + compiled clear setters.
            for (int i = 0; i < 100; i++)
            {
                var cmd = mgr.GetCommand(cmdType);
                mgr.ReturnCommand(cmdType, cmd);
            }

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long startAllocations = System.GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 5000; i++)
            {
                var cmd = mgr.GetCommand(cmdType);
                mgr.ReturnCommand(cmdType, cmd);
            }

            long allocatedBytes = System.GC.GetAllocatedBytesForCurrentThread() - startAllocations;

            Assert.LessOrEqual(allocatedBytes, 128,
                $"CommandPoolManager Get/Return allocated {allocatedBytes} bytes in steady state. Expected ~0.");
        }

        [Test]
        public void Benchmark_SignalFire_WithSubscriberNs()
        {
            const int warmup = 2000;
            const int iterations = 20000;
            int received = 0;
            _signalBus.Subscribe<PerfSignal>(_ => received++);

            for (int i = 0; i < warmup; i++)
                _signalBus.Fire(new PerfSignal(i));

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            double nsPerDispatch = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            UnityEngine.Debug.Log($"[Nexus Benchmark] SignalBus.Fire with 1 subscriber: {nsPerDispatch:F2} ns/dispatch over {iterations} dispatches (total {sw.ElapsedMilliseconds} ms)");

            Assert.AreEqual(iterations + warmup, received);
            Assert.Less(nsPerDispatch, 30000, "Dispatch with a subscriber should stay well under 30us.");
        }
    }
}
