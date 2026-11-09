using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public struct UnheardSignal { }

    public class PerfCommand : ICommand<PerfSignal>
    {
        [Inject] public TestCounter Counter;
        public void Execute(PerfSignal signal) { Counter.Value++; }
    }

    public static class Program
    {
        internal static readonly string TempRoot = Path.Combine(Path.GetTempPath(),
            "nexus-bench-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));

        static Program()
        {
            Directory.CreateDirectory(TempRoot);
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                // Loaded fixture assemblies can remain locked on Windows; cleanup is best-effort.
                try { Directory.Delete(TempRoot, true); } catch { }
            };
        }

        private static NexusDI _container;
        private static CommandPoolManager _poolManager;
        private static SignalBus _signalBus;
        private static TestCounter _counter;
        private static int _failures;

        // Issue #77: the harness compiles without UNITY_EDITOR and its Debug.isDebugBuild is false, as in a release player. It runs
        // first, before any suite assigns Enabled; the first context registered applies the defaults.
        private static void ReleasePlayer_MonitorsStayOffUntilTheGameTurnsThemOn()
        {
            var context = ContextFactory.Create();
            bool performance = PerformanceMonitor.Enabled, network = NetworkMonitor.Enabled;
            context.Dispose();
            Report(nameof(ReleasePlayer_MonitorsStayOffUntilTheGameTurnsThemOn), !performance && !network,
                $"PerformanceMonitor.Enabled={performance} NetworkMonitor.Enabled={network} (expected both false)");
        }

        // Issue #140: the development defaults and diagnostics follow NexusBuild at run time, not the DEVELOPMENT_BUILD symbol
        // Unity 6000.6 deprecates. The harness is a release build (its Debug.isDebugBuild is false); the override runs both.
        private static void DevelopmentBuild_DefaultsAndDiagnostics_FollowTheBuild()
        {
            var failures = new List<string>();
            try
            {
                foreach (bool development in new[] { false, true })
                {
                    NexusBuild.OverrideForTests(development);
                    string build = development ? "development" : "release";

                    if (PerformanceMonitor.DefaultEnabled != development || NetworkMonitor.DefaultEnabled != development)
                        failures.Add($"{build}: monitor defaults performance={PerformanceMonitor.DefaultEnabled} network={NetworkMonitor.DefaultEnabled}");

                    if (NexusRuntime.CurrentContext != null)
                        failures.Add($"{build}: a context is still registered, so the fallback logger cannot be read");
                    else if (NexusRuntime.Logger.IsEnabled != development)
                        failures.Add($"{build}: fallback logger IsEnabled={NexusRuntime.Logger.IsEnabled}");

                    var service = new Nexus.Core.Services.LoggerService();
                    service.InitializeAsync(CancellationToken.None);
                    bool serviceEnabled = service.IsEnabled;
                    service.OnDispose();
                    if (serviceEnabled != development) failures.Add($"{build}: LoggerService IsEnabled={serviceEnabled} after InitializeAsync");

                    var ctx = NexusTestHarness.CreateContext();
                    try
                    {
                        var logger = new SvcLoggerRecorder();
                        ctx.Context.Container.BindInstance<Nexus.Core.Services.ILoggerService>(logger);
                        ctx.Dispatch(new UnheardSignal());
                        ctx.Dispatch(new UnheardSignal());
                        int warnings = logger.Messages.FindAll(m => m.Contains("has no subscribers")).Count;
                        if (warnings != (development ? 1 : 0))
                            failures.Add($"{build}: {warnings} no-subscriber warnings for two fires (expected {(development ? 1 : 0)})");
                    }
                    finally
                    {
                        ctx.Dispose();
                    }
                }
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
            }
            Report(nameof(DevelopmentBuild_DefaultsAndDiagnostics_FollowTheBuild), failures.Count == 0,
                failures.Count == 0 ? "release and development: monitor defaults, fallback logger, LoggerService, one no-subscriber warning" : string.Join("; ", failures));
        }

        // Issue #309: a setter call pinned the fallback logger for the rest of the run, so a test that expects a release player's
        // logger after Reset() read the pinned value (SignalBusTests pinned it enabled). Reset must let it follow the build again.
        private static void FallbackLogger_ResetLetsItFollowTheBuildAgain()
        {
            NexusRuntime.Reset();
            bool ok;
            try
            {
                NexusRuntime.Logger.IsEnabled = true;
                NexusBuild.OverrideForTests(false);
                NexusRuntime.Reset();
                ok = !NexusRuntime.Logger.IsEnabled;
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
                NexusRuntime.Reset();
            }
            Report(nameof(FallbackLogger_ResetLetsItFollowTheBuildAgain), ok,
                ok ? "IsEnabled=true set, Reset, release build: the fallback logger is disabled again" : "the fallback logger kept IsEnabled=true after Reset in a release build");
        }

        private static void SuppressUnheardSignalHints_RepeatedFiresKeepOtherDiagnostics()
        {
            try
            {
                NexusBuild.OverrideForTests(true);
                var logger = new SvcLoggerRecorder();
                using var ctx = NexusTestHarness.CreateContext(builder =>
                {
                    builder.BindInstance<Nexus.Core.Services.ILoggerService>(logger);
                    builder.SuppressUnheardSignalHints();
                });
                for (int i = 0; i < 10; i++)
                {
                    ctx.Dispatch(new UnheardSignal());
                    ctx.Dispatch(new PerfSignal(i));
                }
                const string diagnostic = "336: unrelated warning still arrives";
                NexusRuntime.Logger.LogWarning(diagnostic);
                int hints = logger.Messages.FindAll(m => m.Contains("has no subscribers")).Count;
                bool ok = hints == 0 && logger.IsEnabled && ReferenceEquals(NexusRuntime.Logger, logger)
                    && logger.Messages.Contains(diagnostic);
                Report(nameof(SuppressUnheardSignalHints_RepeatedFiresKeepOtherDiagnostics), ok,
                    $"hints={hints} expected=0 for 20 fires/two types; loggerEnabled={logger.IsEnabled}; " +
                    $"unrelatedWarning={logger.Messages.Contains(diagnostic)}" +
                    (ok ? "" : $"; recorded=[{string.Join(" | ", logger.Messages)}]"));
            }
            finally { NexusBuild.OverrideForTests(null); }
        }

        private static void SuppressUnheardSignalHints_DoesNotAffectParentChildOrSibling()
        {
            var details = new List<string>();
            bool ok = true;
            try
            {
                NexusBuild.OverrideForTests(true);
                foreach (bool suppressParent in new[] { true, false })
                {
                    using var parent = NexusTestHarness.CreateContext();
                    var logger = new SvcLoggerRecorder();
                    parent.Builder.BindInstance<Nexus.Core.Services.ILoggerService>(logger);
                    using var child = NexusTestHarness.CreateChildContext(parent);
                    using var sibling = NexusTestHarness.CreateContext();
                    (suppressParent ? parent.Builder : child.Builder).SuppressUnheardSignalHints();

                    int[] hints = new int[3];
                    var contexts = new[] { parent, child, sibling };
                    for (int i = 0; i < contexts.Length; i++)
                    {
                        int before = logger.Messages.Count;
                        for (int fire = 0; fire < 3; fire++)
                        {
                            contexts[i].Dispatch(new UnheardSignal());
                            contexts[i].Dispatch(new PerfSignal(fire));
                        }
                        hints[i] = logger.Messages.Count - before;
                    }
                    ok &= hints[0] == (suppressParent ? 0 : 2) && hints[1] == (suppressParent ? 2 : 0)
                        && hints[2] == 2 && logger.IsEnabled;
                    details.Add($"suppressed={(suppressParent ? "parent" : "child")}: parent/child/sibling hints={string.Join("/", hints)}");
                }
            }
            finally { NexusBuild.OverrideForTests(null); }
            Report(nameof(SuppressUnheardSignalHints_DoesNotAffectParentChildOrSibling), ok, string.Join("; ", details));
        }

        private static void SuppressUnheardSignalHints_SubscribersAndCommandsStillExecute()
        {
            try
            {
                NexusBuild.OverrideForTests(true);
                using var ctx = NexusTestHarness.CreateContext();
                var counter = new TestCounter();
                ctx.Builder.SuppressUnheardSignalHints();
                ctx.Builder.BindInstance(counter);
                ctx.Builder.BindCommand<PerfSignal, PerfCommand>();
                int received = 0, lastValue = -1;
                using var subscription = ctx.Context.SignalBus.Subscribe<PerfSignal>(signal =>
                {
                    received++;
                    lastValue = signal.Index;
                });
                ctx.Dispatch(new PerfSignal(42));
                ctx.Dispatch(new PerfSignal(99));
                Report(nameof(SuppressUnheardSignalHints_SubscribersAndCommandsStillExecute),
                    counter.Value == 2 && received == 2 && lastValue == 99,
                    $"commands={counter.Value} subscribers={received} expected=2 each; lastValue={lastValue} expected=99");
            }
            finally { NexusBuild.OverrideForTests(null); }
        }

        private static void SuppressUnheardSignalHints_WarmedFireHasZeroGCAllocations()
        {
            try
            {
                NexusBuild.OverrideForTests(true);
                using var ctx = NexusTestHarness.CreateContext();
                var logger = new SvcLoggerRecorder();
                ctx.Builder.BindInstance<Nexus.Core.Services.ILoggerService>(logger);
                ctx.Builder.SuppressUnheardSignalHints();
                for (int i = 0; i < 100; i++) ctx.Context.SignalBus.Fire(new UnheardSignal());

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) ctx.Context.SignalBus.Fire(new UnheardSignal());
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                Report(nameof(SuppressUnheardSignalHints_WarmedFireHasZeroGCAllocations),
                    allocated == 0 && logger.IsEnabled && logger.Messages.Count == 0,
                    $"allocated={allocated} bytes for 5000 warmed fires (limit ==0); loggerEnabled={logger.IsEnabled}; hints={logger.Messages.Count}");
            }
            finally { NexusBuild.OverrideForTests(null); }
        }

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
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--hang-repro")
            {
                return PlayModeHangRepro.Run();
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--coverage")
            {
                int rc = CoverageReport.Run(json);
                if (json) EmitJson();
                return rc;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--framework-closure")
            {
                int rc = FrameworkClosureSuite.Run();
                if (json) EmitJson();
                return rc == 0 ? 0 : 1;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--consumer-persistence")
            {
                int rc = ConsumerPersistenceSuite.Run();
                if (json) EmitJson();
                return rc == 0 ? 0 : 1;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--framework-audit")
            {
                int rc = FrameworkAuditSuite.Run(cmdArgs.Count > 1 ? cmdArgs[1] : null);
                if (json) EmitJson();
                return rc == 0 ? 0 : 1;
            }
            if (cmdArgs.Count > 0 && cmdArgs[0] == "--codegen")
            {
                int rc = CodeGenSuite.Run();
                if (json) EmitJson();
                return rc == 0 ? 0 : 1;
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
            Run("ReleasePlayer_MonitorsStayOffUntilTheGameTurnsThemOn", ReleasePlayer_MonitorsStayOffUntilTheGameTurnsThemOn);
            Run("DevelopmentBuild_DefaultsAndDiagnostics_FollowTheBuild", DevelopmentBuild_DefaultsAndDiagnostics_FollowTheBuild);
            Run("FallbackLogger_ResetLetsItFollowTheBuildAgain", FallbackLogger_ResetLetsItFollowTheBuildAgain);
            Run("SuppressUnheardSignalHints_RepeatedFiresKeepOtherDiagnostics", SuppressUnheardSignalHints_RepeatedFiresKeepOtherDiagnostics);
            Run("SuppressUnheardSignalHints_DoesNotAffectParentChildOrSibling", SuppressUnheardSignalHints_DoesNotAffectParentChildOrSibling);
            Run("SuppressUnheardSignalHints_SubscribersAndCommandsStillExecute", SuppressUnheardSignalHints_SubscribersAndCommandsStillExecute);
            Run("SuppressUnheardSignalHints_WarmedFireHasZeroGCAllocations", SuppressUnheardSignalHints_WarmedFireHasZeroGCAllocations);
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
            _failures += LifetimeScopeSuite.Run();
            _failures += NewFeatureSuite.Run();
            _failures += ConstructorParameterSuite.Run();
            _failures += ObservableOperatorsSuite.Run();
            _failures += DetachedCommandSuite.Run();
            _failures += GCAuditSuite.Run();
            _failures += TeardownLeakSuite.Run();
            _failures += ScreenStackSuite.Run();
            _failures += FixVerificationSuite.Run();
            _failures += ReviewR1Suite.Run();
            _failures += SignalsReviewSuite.Run();
            _failures += EvidenceSuite.Run();
            _failures += AdsCoreSuite.Run();
            _failures += DurableRewardSuite.Run();
            _failures += EconomyUnreadableSuite.Run();
            _failures += ConsumerPersistenceSuite.Run();
            _failures += FrameworkClosureSuite.Run();
            _failures += FrameworkAuditSuite.Run();
            _failures += CounterSampleSuite.Run();
            // Last: creates real Contexts and must not disturb earlier suites' assumptions
            // about active-context counts or trace-buffer state.
            _failures += ServiceGraphSuite.Run();
            // Absolute last: the generated binder registers injectors and typed command
            // dispatchers for every harness type; CodeGenSuite resets them in teardown.
            _failures += CodeGenSuite.Run();
            return _failures;
        }
    }
}
