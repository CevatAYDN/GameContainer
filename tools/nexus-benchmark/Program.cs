// Standalone benchmark harness for the Nexus runtime — replicates every assertion in
// Nexus/Packages/com.nexus.core/Tests/Runtime/PerformanceTests.cs (dispatch timing,
// hot-path ns limits, steady-state zero-GC, pool reuse, and the
// CommandPoolManager Get/Return zero-alloc regression) so the same numbers can be
// produced on plain .NET when the Unity editor (6000.5) is unavailable.
//
// Run: dotnet run -c Release            (benchmarks + recovery regression)
//      dotnet run -c Release -- --alloc-diag   (allocation-source diagnostics)
//      dotnet run -c Release -- --pool-split   (pool round-trip split diagnostics)
//      dotnet run -c Release -- --soak [N]     (repeat the full pipeline N times and
//                                               watch heap/thread/static-cache growth)

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.FSM;
using Nexus.Netcode;

namespace NexusBench
{
    public class BenchStateA : IGameState
    {
        public ValueTask OnEnterAsync(object payload, CancellationToken ct) => default;
        public ValueTask OnExitAsync(CancellationToken ct) => default;
        public void OnTick(float deltaTime) {}
    }

    public class BenchStateB : IGameState
    {
        public ValueTask OnEnterAsync(object payload, CancellationToken ct) => default;
        public ValueTask OnExitAsync(CancellationToken ct) => default;
        public void OnTick(float deltaTime) {}
    }

    public struct NetcodePerfSignal : INetworkSignal
    {
        public int Tick;
        public NetcodePerfSignal(int tick) => Tick = tick;
    }

    public readonly struct PerfSignal
    {
        public readonly int Index;
        public PerfSignal(int index) => Index = index;
    }

    public class TestCounter { public int Value; }

    public class PerfCommand : ICommand<PerfSignal>
    {
        [Inject] public TestCounter Counter;
        public void Execute(PerfSignal signal) { Counter.Value++; }
    }

    public static class Program
    {
        private static NexusDI _container;
        private static CommandPoolManager _poolManager;
        private static SignalBus _signalBus;
        private static TestCounter _counter;
        private static int _failures;

        private static void Setup()
        {
            _counter = new TestCounter();
            _container = new NexusDI();
            _container.BindInstance(_counter);
            _container.Bind<PerfCommand>(isSingleton: false);
            _poolManager = new CommandPoolManager(_container);
            _signalBus = new SignalBus(_container, _poolManager, new MockContext());
            _signalBus.RegisterCommand(typeof(PerfSignal), typeof(PerfCommand), ExecutionMode.Sequential, 0, false);
        }

        private static void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!ok) _failures++;
        }

        private static void Dispatch1000Signals_CompletesUnderTime()
        {
            const int count = 1000;
            // Warmup JIT
            for (int i = 0; i < 10; i++) _signalBus.Fire(new PerfSignal(i));
            _counter.Value = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            bool okCount = _counter.Value == count;
            bool okTime = sw.ElapsedMilliseconds < 50;
            Report("Dispatch1000Signals_CompletesUnderTime", okCount && okTime,
                $"counter={_counter.Value} expected={count}, elapsed={sw.ElapsedMilliseconds}ms (limit <50ms)");
        }

        private static void Subscribe1000AndFire_AllReceived()
        {
            const int count = 1000;
            int received = 0;
            _signalBus.Subscribe<PerfSignal>(sig => received++);
            for (int i = 0; i < count; i++) _signalBus.Fire(new PerfSignal(i));
            Report("Subscribe1000AndFire_AllReceived", received == count,
                $"received={received} expected={count}");
        }

        private static void CommandPool_ReusesInstances()
        {
            _signalBus.Fire(new PerfSignal(1));
            _signalBus.Fire(new PerfSignal(2));
            bool twoOk = _counter.Value == 2;
            for (int i = 0; i < 100; i++) _signalBus.Fire(new PerfSignal(i));
            bool hundredOk = _counter.Value == 102;
            Report("CommandPool_ReusesInstances", twoOk && hundredOk,
                $"counter={_counter.Value} expected=102");
        }

        private static void CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate()
        {
            // Replicates CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate in
            // Tests/Runtime/PerformanceTests.cs: a fresh manager on the bound container,
            // warmed up, then 5000 Get/Return round-trips measured for allocations.
            var manager = new CommandPoolManager(_container);
            var cmdType = typeof(PerfCommand);

            // Warm up: JIT + pool growth + HashSet capacity + compiled clear setters.
            for (int i = 0; i < 100; i++)
            {
                var cmd = manager.GetCommand(cmdType);
                manager.ReturnCommand(cmdType, cmd);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                var cmd = manager.GetCommand(cmdType);
                manager.ReturnCommand(cmdType, cmd);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Report("CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate", allocated <= 128,
                $"allocated={allocated} bytes for 5000 Get/Return ops (limit <=128)");
        }

        private static void SteadyState_HasZeroGCAllocations()
        {
            for (int i = 0; i < 100; i++) _signalBus.Fire(new PerfSignal(i));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) _signalBus.Fire(new PerfSignal(i));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Report("SteadyState_HasZeroGCAllocations", allocated <= 128,
                $"allocated={allocated} bytes for 5000 dispatches (limit <=128)");
        }

        private static void HighFrequency_Performance_StressTest()
        {
            const int count = 50000;
            for (int i = 0; i < 100; i++) _signalBus.Fire(new PerfSignal(i));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();
            Console.WriteLine($"[Nexus Benchmark] 50,000 dispatches completed in {sw.ElapsedMilliseconds} ms.");
            Report("HighFrequency_Performance_StressTest", sw.ElapsedMilliseconds < 800,
                $"elapsed={sw.ElapsedMilliseconds}ms (limit <800ms)");
        }

        private static void Benchmark_SignalFire_HotPathNs()
        {
            const int warmup = 2000;
            const int iterations = 20000;
            for (int i = 0; i < warmup; i++) _signalBus.Fire(new PerfSignal(i));

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            double nsPerDispatch = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            Console.WriteLine($"[Nexus Benchmark] SignalBus.Fire hot-path: {nsPerDispatch:F2} ns/dispatch over {iterations} dispatches (total {sw.ElapsedMilliseconds} ms)");
            Report("Benchmark_SignalFire_HotPathNs", nsPerDispatch < 25000,
                $"{nsPerDispatch:F2} ns/dispatch (limit <25000ns)");
        }

        private static void Benchmark_SignalFire_WithSubscriberNs()
        {
            const int warmup = 2000;
            const int iterations = 20000;
            int received = 0;
            _signalBus.Subscribe<PerfSignal>(_ => received++);

            for (int i = 0; i < warmup; i++) _signalBus.Fire(new PerfSignal(i));

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            double nsPerDispatch = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            Console.WriteLine($"[Nexus Benchmark] SignalBus.Fire with 1 subscriber: {nsPerDispatch:F2} ns/dispatch over {iterations} dispatches (total {sw.ElapsedMilliseconds} ms)");
            bool ok = received == iterations + warmup && nsPerDispatch < 30000;
            Report("Benchmark_SignalFire_WithSubscriberNs", ok,
                $"{nsPerDispatch:F2} ns/dispatch (limit <30000ns), received={received} expected={iterations + warmup}");
        }

        private static void Run(string name, Action action)
        {
            try
            {
                Setup();
                Console.WriteLine();
                Console.WriteLine($"[Nexus Benchmark] === {name} ===");
                action();
            }
            catch (Exception ex)
            {
                Report(name, false, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                TearDown();
            }
        }

        private static void FSM_StateTransition_Performance()
        {
            using var fsm = new GameStateMachine();
            fsm.RegisterState(new BenchStateA());
            fsm.RegisterState(new BenchStateB());

            // Warmup
            for (int i = 0; i < 50; i++)
            {
                fsm.ChangeStateAsync<BenchStateA>().GetAwaiter().GetResult();
                fsm.ChangeStateAsync<BenchStateB>().GetAwaiter().GetResult();
            }

            var sw = Stopwatch.StartNew();
            const int transitions = 5000;
            for (int i = 0; i < transitions; i++)
            {
                if (i % 2 == 0) fsm.ChangeStateAsync<BenchStateA>().GetAwaiter().GetResult();
                else fsm.ChangeStateAsync<BenchStateB>().GetAwaiter().GetResult();
            }
            sw.Stop();

            double nsPerTransition = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / transitions;
            Console.WriteLine($"[Nexus Benchmark] FSM state transitions: {nsPerTransition:F2} ns/transition over {transitions} transitions");
            Report("FSM_StateTransition_Performance", nsPerTransition < 50000, $"{nsPerTransition:F2} ns/transition (limit <50000ns)");
        }

        private static void HybridQueue_ThreadSafe_ZeroGC()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            var queue = new HybridQueue(bus);

            // Warmup steady-state queue operations
            for (int b = 0; b < 10; b++)
            {
                for (int i = 0; i < 10; i++) queue.EnqueueThreadSafe(new PerfSignal(i));
                queue.DrainThreadSafe();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int batches = 500;
            const int perBatch = 10;
            for (int b = 0; b < batches; b++)
            {
                for (int i = 0; i < perBatch; i++) queue.EnqueueThreadSafe(new PerfSignal(i));
                queue.DrainThreadSafe();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Benchmark] HybridQueue ThreadSafe steady-state: {allocated} bytes for {batches * perBatch} enqueues/drains");
            Report("HybridQueue_ThreadSafe_ZeroGC", allocated <= 128, $"allocated={allocated} bytes for {batches * perBatch} ops (limit <=128)");
        }

        private static void Netcode_Rollback_And_Replay_ZeroGC()
        {
            var history = new NetworkSignalHistory<NetcodePerfSignal>(1024);
            var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());

            // Warmup steady-state history operations
            for (int c = 0; c < 50; c++)
            {
                for (int t = 0; t < 5; t++) history.Add(c * 5 + t, new NetcodePerfSignal(c * 5 + t));
                history.ReplaySignals(c * 5 + 2, bus);
                history.RemoveSignalsAfter(c * 5 + 3);
                history.Prune(c * 5 + 1);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int cycles = 1000;
            for (int c = 50; c < 50 + cycles; c++)
            {
                // Simulate 5 ticks of network signals
                for (int t = 0; t < 5; t++) history.Add(c * 5 + t, new NetcodePerfSignal(c * 5 + t));
                // Simulate rollback replay of 3 ticks
                history.ReplaySignals(c * 5 + 2, bus);
                // Simulate rollback compaction
                history.RemoveSignalsAfter(c * 5 + 3);
                // Prune confirmed ticks
                history.Prune(c * 5 + 1);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Benchmark] Netcode Rollback/Replay steady-state: {allocated} bytes for {cycles} rollback cycles");
            Report("Netcode_Rollback_And_Replay_ZeroGC", allocated <= 128, $"allocated={allocated} bytes for {cycles} cycles (limit <=128)");
        }

        private static void ErrorCollection_Concurrent_StressTest()
        {
            ErrorCollection.Clear();
            var exceptions = new Exception[100];
            for (int i = 0; i < exceptions.Length; i++) exceptions[i] = new InvalidOperationException($"Err {i}");

            var threads = new Thread[4];
            const int opsPerThread = 10000;

            var sw = Stopwatch.StartNew();
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        ErrorCollection.CollectException(exceptions[i % exceptions.Length]);
                    }
                });
                threads[t].Start();
            }
            for (int t = 0; t < threads.Length; t++) threads[t].Join();
            sw.Stop();

            var recent = ErrorCollection.GetRecentErrors();
            var frequent = ErrorCollection.GetFrequentErrors();
            ErrorCollection.ClearBefore(DateTime.UtcNow.AddMinutes(1));

            Console.WriteLine($"[Nexus Benchmark] ErrorCollection: 40,000 concurrent exceptions in {sw.ElapsedMilliseconds} ms (recent={recent.Length}, frequent={frequent.Length})");
            Report("ErrorCollection_Concurrent_StressTest", sw.ElapsedMilliseconds < 1000 && recent.Length > 0,
                $"elapsed={sw.ElapsedMilliseconds}ms for 40k exceptions (limit <1000ms)");
        }

        public static int Main(string[] args)
        {
            Console.WriteLine($"[Nexus Benchmark] Runtime: {Environment.Version}, {Environment.ProcessorCount} cores, {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Console.WriteLine($"[Nexus Benchmark] Process: {Process.GetCurrentProcess().ProcessName}");

            if (args.Length > 0 && args[0] == "--alloc-diag")
            {
                return Diagnostics.Run() ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--pool-split")
            {
                return PoolSplit.Run();
            }
            if (args.Length > 0 && args[0] == "--soak")
            {
                int iterations = 10;
                if (args.Length > 1 && int.TryParse(args[1], out int n) && n > 0) iterations = n;
                return SoakMode.Run(iterations);
            }

            int failures = RunAll();

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] ALL BENCHMARKS PASSED ✓"
                : $"[Nexus Benchmark] {failures} BENCHMARK(S) FAILED ✗");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Full pipeline: benchmarks + recovery regression + architecture stress suite.</summary>
        public static int RunAll()
        {
            _failures = 0;
            Run("Dispatch1000Signals_CompletesUnderTime", Dispatch1000Signals_CompletesUnderTime);
            Run("Subscribe1000AndFire_AllReceived", Subscribe1000AndFire_AllReceived);
            Run("CommandPool_ReusesInstances", CommandPool_ReusesInstances);
            Run("CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate", CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate);
            Run("SteadyState_HasZeroGCAllocations", SteadyState_HasZeroGCAllocations);
            Run("HighFrequency_Performance_StressTest", HighFrequency_Performance_StressTest);
            Run("Benchmark_SignalFire_HotPathNs", Benchmark_SignalFire_HotPathNs);
            Run("Benchmark_SignalFire_WithSubscriberNs", Benchmark_SignalFire_WithSubscriberNs);
            Run("FSM_StateTransition_Performance", FSM_StateTransition_Performance);
            Run("HybridQueue_ThreadSafe_ZeroGC", HybridQueue_ThreadSafe_ZeroGC);
            Run("Netcode_Rollback_And_Replay_ZeroGC", Netcode_Rollback_And_Replay_ZeroGC);
            Run("ErrorCollection_Concurrent_StressTest", ErrorCollection_Concurrent_StressTest);

            _failures += RecoveryRegression.Run();
            _failures += FullArchitectureStressSuite.Run();
            _failures += FuzzSuite.Run();
            _failures += CrossThreadSuite.Run();
            return _failures;
        }
    }
}
