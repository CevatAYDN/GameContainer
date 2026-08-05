using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
            ResultSink.Capture("Benchmarks", name, ok, detail);
            if (!ok) _failures++;
        }

        private static void Dispatch1000Signals_CompletesUnderTime()
        {
            const int count = 1000;
            for (int i = 0; i < 10; i++) _signalBus.Fire(new PerfSignal(i));
            _counter.Value = 0;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();

            bool okCount = _counter.Value == count;
            bool okTime = sw.ElapsedMilliseconds < 10;
            Report("Dispatch1000Signals_CompletesUnderTime", okCount && okTime,
                $"counter={_counter.Value} expected={count}, elapsed={sw.ElapsedMilliseconds}ms (limit <10ms)");
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
            var manager = new CommandPoolManager(_container);
            var cmdType = typeof(PerfCommand);

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

            // REVIEW FIX: tightened the assertion from <=128 to ==0 bytes. The framework's
            // "0 GC allocation" claim means steady-state dispatch must allocate NOTHING.
            // Kept the per-thread API (GetAllocatedBytesForCurrentThread): the harness runs
            // dispatch synchronously on the main thread, so per-thread measurement is the
            // correct scope. The whole-process API (GetTotalAllocatedBytes) counts unrelated
            // background-thread allocations (e.g. GC finalizer threads, thread-pool house-
            // keeping) and produces flaky false positives.
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) _signalBus.Fire(new PerfSignal(i));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Report("SteadyState_HasZeroGCAllocations", allocated == 0,
                $"allocated={allocated} bytes for 5000 dispatches (limit ==0)");
        }

        private static void HighFrequency_Performance_StressTest()
        {
            const int count = 50000;
            for (int i = 0; i < 100; i++) _signalBus.Fire(new PerfSignal(i));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) _signalBus.Fire(new PerfSignal(i));
            sw.Stop();
            Console.WriteLine($"[Nexus Benchmark] 50,000 dispatches completed in {sw.ElapsedMilliseconds} ms.");
            Report("HighFrequency_Performance_StressTest", sw.ElapsedMilliseconds < 200,
                $"elapsed={sw.ElapsedMilliseconds}ms (limit <200ms)");
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
            // Post-optimization baseline is ~360-440 ns/dispatch; 3000 ns keeps ~7-8x CI headroom.
            Report("Benchmark_SignalFire_HotPathNs", nsPerDispatch < 3000,
                $"{nsPerDispatch:F2} ns/dispatch (limit <3000ns)");
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
            bool ok = received == iterations + warmup && nsPerDispatch < 4000;
            Report("Benchmark_SignalFire_WithSubscriberNs", ok,
                $"{nsPerDispatch:F2} ns/dispatch (limit <4000ns), received={received} expected={iterations + warmup}");
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
            // Baseline ~190 ns/transition; 3000 ns keeps ~15x CI headroom.
            Report("FSM_StateTransition_Performance", nsPerTransition < 3000, $"{nsPerTransition:F2} ns/transition (limit <3000ns)");
        }

        private static void HybridQueue_ThreadSafe_ZeroGC()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            var queue = new HybridQueue(bus);

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
                for (int t = 0; t < 5; t++) history.Add(c * 5 + t, new NetcodePerfSignal(c * 5 + t));
                history.ReplaySignals(c * 5 + 2, bus);
                history.RemoveSignalsAfter(c * 5 + 3);
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
            // Baseline ~30 ms; 500 ms is generous CI headroom for a 4-thread lock-heavy path.
            Report("ErrorCollection_Concurrent_StressTest", sw.ElapsedMilliseconds < 500 && recent.Length > 0,
                $"elapsed={sw.ElapsedMilliseconds}ms for 40k exceptions (limit <500ms)");
        }

        public static int Main(string[] args)
        {
            int pinCore = -1;
            bool json = false;
            var cmdArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--json":
                        json = true;
                        break;
                    case "--pin-cpu":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int core) && core >= 0)
                        {
                            pinCore = core;
                            i++;
                        }
                        else
                        {
                            Console.WriteLine("[Nexus Benchmark] WARN: --pin-cpu needs a core index (0..N-1); ignoring");
                        }
                        break;
                    default:
                        cmdArgs.Add(args[i]);
                        break;
                }
            }

            Console.WriteLine($"[Nexus Benchmark] Runtime: {Environment.Version}, {Environment.ProcessorCount} cores, {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Console.WriteLine($"[Nexus Benchmark] Process: {Process.GetCurrentProcess().ProcessName}");
            Console.WriteLine($"[Nexus Benchmark] OS: {RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} " +
                $"serverGC={System.Runtime.GCSettings.IsServerGC}");

            if (pinCore >= 0)
            {
                if (pinCore < Environment.ProcessorCount)
                {
                    var proc = Process.GetCurrentProcess();
                    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                    {
                        try
                        {
                            proc.ProcessorAffinity = (IntPtr)(1L << pinCore);
                        }
                        catch
                        {
                            Console.WriteLine($"[Nexus Benchmark] WARN: could not set ProcessorAffinity to core {pinCore}");
                        }
                    }
                    proc.PriorityClass = ProcessPriorityClass.High;
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
                    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                        Console.WriteLine($"[Nexus Benchmark] Pinned to core {pinCore} (affinity=0x{proc.ProcessorAffinity.ToInt64():X}, priority=High)");
                    else
                        Console.WriteLine($"[Nexus Benchmark] Pinned to core {pinCore} (affinity=n/a — unsupported on this OS, priority=High)");
                }
                else
                {
                    Console.WriteLine($"[Nexus Benchmark] WARN: core {pinCore} out of range (0..{Environment.ProcessorCount - 1}); not pinning");
                }
            }

            if (cmdArgs.Count > 0 && cmdArgs[0] == "--alloc-diag")
            {
                return Diagnostics.Run() ? 0 : 1;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--pool-split")
            {
                return PoolSplit.Run();
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--coverage")
            {
                int rc = CoverageReport.Run(json);
                if (json) EmitJson();
                return rc;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--soak")
            {
                int iterations = 10;
                if (cmdArgs.Count > 1 && int.TryParse(cmdArgs[1], out int n) && n > 0) iterations = n;
                int rc = SoakMode.Run(iterations);
                if (json) EmitJson();
                return rc;
            }

            int failures = RunAll();

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] ALL BENCHMARKS PASSED ✓"
                : $"[Nexus Benchmark] {failures} BENCHMARK(S) FAILED ✗");
            if (json) EmitJson();
            return failures == 0 ? 0 : 1;
        }

        private static void EmitJson()
        {
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] JSON REPORT:");
            Console.WriteLine(ResultSink.ToJson());
        }

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
            _failures += AuditFixRegression.Run();
            _failures += FullArchitectureStressSuite.Run();
            _failures += FuzzSuite.Run();
            _failures += CrossThreadSuite.Run();
            _failures += GameSessionSuite.Run();
            _failures += ServicesSuite.Run();
            _failures += BinderSuite.Run();
            _failures += RegistrySuite.Run();
            _failures += ConcurrentDiffSuite.Run();
            _failures += CapabilitiesSuite.Run();
            _failures += LifecycleSuite.Run();
            _failures += GCAuditSuite.Run();
            _failures += TeardownLeakSuite.Run();
            _failures += FixVerificationSuite.Run();
            _failures += EvidenceSuite.Run();
            // Last: creates real Contexts and must not disturb earlier suites' assumptions
            // about active-context counts or trace-buffer state.
            _failures += DemoCompatibilitySuite.Run();
            return _failures;
        }
    }
}
