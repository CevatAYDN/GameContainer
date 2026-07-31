using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.FSM;
using Nexus.Netcode;

namespace NexusBench
{
    // =========================================================================
    // TEST TYPES FOR FULL ARCHITECTURE STRESS SUITE
    // =========================================================================

    public struct DeepSignal1 { public int Val; }
    public struct DeepSignal2 { public int Val; }
    public struct DeepSignal3 { public int Val; }

    // Use static bus reference — no [Inject] = no DI allocation on hot path
    public class DeepCommand1 : ICommand<DeepSignal1>, IResettable
    {
        public static SignalBus SharedBus;
        public void Execute(DeepSignal1 signal) => SharedBus.Fire(new DeepSignal2 { Val = signal.Val + 1 });
        public void Reset() { }
    }

    public class DeepCommand2 : ICommand<DeepSignal2>, IResettable
    {
        public static SignalBus SharedBus;
        public void Execute(DeepSignal2 signal) => SharedBus.Fire(new DeepSignal3 { Val = signal.Val + 1 });
        public void Reset() { }
    }

    public class DeepCommand3 : ICommand<DeepSignal3>, IResettable
    {
        public static int ExecutedCount;
        public void Execute(DeepSignal3 signal) => ExecutedCount++;
        public void Reset() { }
    }

    [CrossContext]
    public struct BroadcastSignal { public int Id; }

    public class CrossCmd : ICommand<BroadcastSignal>
    {
        public static int FiredCount;
        public void Execute(BroadcastSignal signal) => Interlocked.Increment(ref FiredCount);
    }

    public struct BenchPluginSignal { public int Val; }
    public class BenchPluginCmd : ICommand<BenchPluginSignal>
    {
        public static int FiredCount;
        public void Execute(BenchPluginSignal signal) => FiredCount++;
    }

    public class SampleInterceptor : ISignalInterceptor
    {
        public static int InterceptCount;
        public bool Intercept(ref object signal)
        {
            InterceptCount++;
            return true; // allow signal
        }
    }

    public class SampleDecorator : ICommandDecorator
    {
        public static int DecorateCount;
        public void DecorateExecute(object command, Action next)
        {
            DecorateCount++;
            next();
        }

        public ValueTask DecorateExecuteAsync(object command, Func<ValueTask> next)
        {
            DecorateCount++;
            return next();
        }
    }

    public class SamplePlugin : INexusPlugin
    {
        public NexusPluginManifest Manifest => new NexusPluginManifest("BenchPlugin", "1.0", PluginCapabilities.SignalInterceptor | PluginCapabilities.CommandDecorator);
        public void OnPluginRegistered(IPluginContext context)
        {
            context.RegisterSignalInterceptor(new SampleInterceptor());
            context.RegisterCommandDecorator(new SampleDecorator());
        }
        public void OnPluginRemoved() {}
    }

    public class DependencyLeaf { public int Value = 42; }
    public class DependencyNode
    {
        [Inject] public DependencyLeaf Leaf;
    }

    /// <summary>Simple IContextResolver that serves a fixed list of contexts. Zero-alloc after warmup.</summary>
    public class ListContextResolver : IContextResolver
    {
        private readonly IReadOnlyList<IContext> _contexts;
        public ListContextResolver(List<IContext> contexts) => _contexts = contexts;
        public IReadOnlyList<IContext> GetActiveContexts() => _contexts;
    }

    // =========================================================================
    // FULL ARCHITECTURE ADVERSARIAL STRESS SUITE IMPLEMENTATION
    // =========================================================================

    public static class FullArchitectureStressSuite
    {
        private static int _failures = 0;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Architecture Stress] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            if (!ok) _failures++;
        }

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Nexus Architecture Stress] STARTING FULL ARCHITECTURE ADVERSARIAL STRESS SUITE");
            Console.WriteLine("===============================================================================");

            Test_NexusDI_Resolution_And_Injection_Stress();
            Test_SignalBus_Deep_Reentrancy_ZeroGC();
            Test_SignalBus_Subscriber_FanOut_1000_Subs();
            Test_CrossContext_MultiHierarchy_Routing_ZeroGC();
            Test_CommandPool_MultiType_GetReturn_ZeroGC();
            Test_GameStateMachine_10k_AsyncTransitions_Stress();
            Test_HybridQueue_MultiThreaded_8Workers_Stress();
            Test_PluginSystem_DecoratorChain_Interceptor_Stress();
            Test_Netcode_Rollback_Replay_HighJitter_ZeroGC();
            Test_ErrorCollection_And_PerfMonitor_Concurrent_Stress();

            Console.WriteLine("===============================================================================");
            Console.WriteLine(_failures == 0
                ? "[Nexus Architecture Stress] ALL 10 ARCHITECTURE STRESS TESTS PASSED ✓"
                : $"[Nexus Architecture Stress] {_failures} STRESS TEST(S) FAILED ✗");
            Console.WriteLine("===============================================================================");
            return _failures;
        }

        // ---------------------------------------------------------------------
        // 1. NexusDI Container Resolution & Injection Stress
        // ---------------------------------------------------------------------
        private static void Test_NexusDI_Resolution_And_Injection_Stress()
        {
            var di = new NexusDI();
            di.Bind<DependencyLeaf>(isSingleton: true);
            di.Bind<DependencyNode>(isSingleton: false);

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                var node = di.Resolve<DependencyNode>();
                di.Inject(node);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sw = Stopwatch.StartNew();
            const int count = 50000;
            for (int i = 0; i < count; i++)
            {
                var node = di.Resolve<DependencyNode>();
                di.Inject(node);
            }
            sw.Stop();

            double nsPerOp = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / count;
            Console.WriteLine($"[Nexus Architecture Stress] NexusDI resolve+inject: {nsPerOp:F2} ns/op over {count} ops");
            Report("1. NexusDI_Resolution_And_Injection_Stress", nsPerOp < 5000, $"{nsPerOp:F2} ns/op (limit <5000ns)");
        }

        // ---------------------------------------------------------------------
        // 2. SignalBus Deep Reentrancy (Signal A -> Signal B -> Signal C) Zero-GC
        // ---------------------------------------------------------------------
        private static void Test_SignalBus_Deep_Reentrancy_ZeroGC()
        {
            var di = new NexusDI();
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());

            // No [Inject] on commands - use static SharedBus to avoid DI field injection allocations
            DeepCommand1.SharedBus = bus;
            DeepCommand2.SharedBus = bus;

            di.Bind<DeepCommand1>(isSingleton: false);
            di.Bind<DeepCommand2>(isSingleton: false);
            di.Bind<DeepCommand3>(isSingleton: false);

            bus.RegisterCommand(typeof(DeepSignal1), typeof(DeepCommand1), ExecutionMode.Sequential, 0, false);
            bus.RegisterCommand(typeof(DeepSignal2), typeof(DeepCommand2), ExecutionMode.Sequential, 0, false);
            bus.RegisterCommand(typeof(DeepSignal3), typeof(DeepCommand3), ExecutionMode.Sequential, 0, false);

            DeepCommand3.ExecutedCount = 0;
            // Warmup
            for (int i = 0; i < 100; i++) bus.Fire(new DeepSignal1 { Val = i });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int dispatches = 5000;
            for (int i = 0; i < dispatches; i++)
            {
                bus.Fire(new DeepSignal1 { Val = i });
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] Deep Reentrancy (3 levels): {allocated} bytes for {dispatches} dispatches (executed={DeepCommand3.ExecutedCount})");
            Report("2. SignalBus_Deep_Reentrancy_ZeroGC", allocated <= 128 && DeepCommand3.ExecutedCount >= dispatches,
                $"allocated={allocated} bytes for {dispatches} 3-level reentrant dispatches (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 3. Subscriber Fan-Out (1 Signal -> 1000 Active Subscribers) Stress
        // ---------------------------------------------------------------------
        private static void Test_SignalBus_Subscriber_FanOut_1000_Subs()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            const int subsCount = 1000;
            int totalCallbacks = 0;

            for (int i = 0; i < subsCount; i++)
            {
                bus.Subscribe<PerfSignal>(_ => Interlocked.Increment(ref totalCallbacks));
            }

            // Warmup
            for (int i = 0; i < 10; i++) bus.Fire(new PerfSignal(i));

            totalCallbacks = 0;
            var sw = Stopwatch.StartNew();
            const int fires = 2000;
            for (int i = 0; i < fires; i++)
            {
                bus.Fire(new PerfSignal(i));
            }
            sw.Stop();

            long expectedCallbacks = (long)fires * subsCount;
            Console.WriteLine($"[Nexus Architecture Stress] 1,000 Subscribers Fan-Out: {expectedCallbacks} callbacks delivered in {sw.ElapsedMilliseconds} ms");
            Report("3. SignalBus_Subscriber_FanOut_1000_Subs", totalCallbacks == expectedCallbacks && sw.ElapsedMilliseconds < 500,
                $"delivered={totalCallbacks} expected={expectedCallbacks}, elapsed={sw.ElapsedMilliseconds}ms (limit <500ms)");
        }

        // ---------------------------------------------------------------------
        // 4. CrossContext Multi-Hierarchy Signal Broadcast Zero-GC
        // ---------------------------------------------------------------------
        private static void Test_CrossContext_MultiHierarchy_Routing_ZeroGC()
        {
            NexusRuntime.Reset();

            // Build two independent contexts
            var rootDi = new NexusDI();
            var rootPool = new CommandPoolManager(rootDi);
            var rootCtx = new MockContext();
            var childCtx = new MockContext();

            // Custom resolver that knows about both contexts
            var knownContexts = new List<IContext> { rootCtx, childCtx };
            var resolver = new ListContextResolver(knownContexts);

            var rootBus = new SignalBus(rootDi, rootPool, rootCtx, resolver);
            rootCtx.SignalBus = rootBus;

            var childDi = new NexusDI();
            var childPool = new CommandPoolManager(childDi);
            var childBus = new SignalBus(childDi, childPool, childCtx, resolver);
            childCtx.SignalBus = childBus;

            childDi.Bind<CrossCmd>(isSingleton: false);
            childBus.RegisterCommand(typeof(BroadcastSignal), typeof(CrossCmd), ExecutionMode.Sequential, 0, false);

            CrossCmd.FiredCount = 0;
            // Warmup: fire on rootBus -> should broadcast to childBus -> CrossCmd.Execute
            for (int i = 0; i < 100; i++) rootBus.Fire(new BroadcastSignal { Id = i });
            CrossCmd.FiredCount = 0; // reset after warmup

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int count = 5000;
            for (int i = 0; i < count; i++)
            {
                rootBus.Fire(new BroadcastSignal { Id = i });
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] CrossContext Broadcast: {allocated} bytes for {count} cross-context signals (fired={CrossCmd.FiredCount})");
            Report("4. CrossContext_MultiHierarchy_Routing_ZeroGC", allocated <= 128 && CrossCmd.FiredCount >= count,
                $"allocated={allocated} bytes for {count} cross-context signals (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 5. CommandPool Multi-Type Checkout & Return Zero-GC
        // ---------------------------------------------------------------------
        private static void Test_CommandPool_MultiType_GetReturn_ZeroGC()
        {
            var di = new NexusDI();
            di.BindInstance(new TestCounter());
            di.Bind<PerfCommand>(isSingleton: false);
            di.Bind<DeepCommand1>(isSingleton: false);
            di.Bind<DeepCommand2>(isSingleton: false);
            var pool = new CommandPoolManager(di);

            var dummyBus = new SignalBus(di, pool, new MockContext());
            di.BindInstance<ISignalBus>(dummyBus);
            di.BindInstance<SignalBus>(dummyBus);
            di.BindInstance(dummyBus);

            // Warmup pool
            var c1 = pool.GetCommand(typeof(PerfCommand));
            var c2 = pool.GetCommand(typeof(DeepCommand1));
            var c3 = pool.GetCommand(typeof(DeepCommand2));
            pool.ReturnCommand(typeof(PerfCommand), c1);
            pool.ReturnCommand(typeof(DeepCommand1), c2);
            pool.ReturnCommand(typeof(DeepCommand2), c3);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int ops = 20000;
            for (int i = 0; i < ops; i++)
            {
                var o1 = pool.GetCommand(typeof(PerfCommand));
                di.Inject(o1);
                pool.ReturnCommand(typeof(PerfCommand), o1);

                var o2 = pool.GetCommand(typeof(DeepCommand1));
                di.Inject(o2);
                pool.ReturnCommand(typeof(DeepCommand1), o2);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] CommandPool Multi-Type Get+Inject+Return: {allocated} bytes for {ops * 2} operations");
            Report("5. CommandPool_MultiType_GetReturn_ZeroGC", allocated <= 128,
                $"allocated={allocated} bytes for {ops * 2} pooled command checkouts (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 6. GameStateMachine 10,000 Async Transitions Stress
        // ---------------------------------------------------------------------
        private static void Test_GameStateMachine_10k_AsyncTransitions_Stress()
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
            const int transitions = 10000;
            for (int i = 0; i < transitions; i++)
            {
                if (i % 2 == 0) fsm.ChangeStateAsync<BenchStateA>().GetAwaiter().GetResult();
                else fsm.ChangeStateAsync<BenchStateB>().GetAwaiter().GetResult();
            }
            sw.Stop();

            double nsPerTransition = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / transitions;
            Console.WriteLine($"[Nexus Architecture Stress] FSM 10,000 State Transitions: {nsPerTransition:F2} ns/transition");
            Report("6. GameStateMachine_10k_AsyncTransitions_Stress", nsPerTransition < 50000,
                $"{nsPerTransition:F2} ns/transition over {transitions} transitions (limit <50000ns)");
        }

        // ---------------------------------------------------------------------
        // 7. HybridQueue Multi-Threaded 8 Worker Threads Enqueue & Drain Stress
        // ---------------------------------------------------------------------
        private static void Test_HybridQueue_MultiThreaded_8Workers_Stress()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            var queue = new HybridQueue(bus);

            const int numWorkers = 8;
            const int enqueuesPerWorker = 10000;
            var threads = new Thread[numWorkers];

            var sw = Stopwatch.StartNew();
            for (int w = 0; w < numWorkers; w++)
            {
                threads[w] = new Thread(() =>
                {
                    for (int i = 0; i < enqueuesPerWorker; i++)
                    {
                        queue.EnqueueThreadSafe(new PerfSignal(i));
                    }
                });
                threads[w].Start();
            }

            int drainedTotal = 0;
            while (drainedTotal < numWorkers * enqueuesPerWorker)
            {
                int prev = queue.ThreadSafeQueueDepth;
                queue.DrainThreadSafe();
                drainedTotal += prev;
                Thread.Sleep(1);
            }

            for (int w = 0; w < numWorkers; w++) threads[w].Join();
            sw.Stop();

            Console.WriteLine($"[Nexus Architecture Stress] HybridQueue 8 Workers Concurrent: {numWorkers * enqueuesPerWorker} enqueues/drains completed in {sw.ElapsedMilliseconds} ms");
            Report("7. HybridQueue_MultiThreaded_8Workers_Stress", queue.TotalEnqueued == numWorkers * enqueuesPerWorker && sw.ElapsedMilliseconds < 2000,
                $"totalEnqueued={queue.TotalEnqueued} totalDrained={queue.TotalDrained}, elapsed={sw.ElapsedMilliseconds}ms (limit <2000ms)");
        }

        // ---------------------------------------------------------------------
        // 8. PluginSystem Decorator Chain & Signal Interceptor Pipeline Stress
        // ---------------------------------------------------------------------
        private static void Test_PluginSystem_DecoratorChain_Interceptor_Stress()
        {
            var plugin = new SamplePlugin();
            var context = new MockContext();
            context.RegisterPlugin(plugin);

            var di = new NexusDI();
            di.Bind<BenchPluginCmd>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, context);

            bus.RegisterCommand(typeof(BenchPluginSignal), typeof(BenchPluginCmd), ExecutionMode.Sequential, 0, false);

            BenchPluginCmd.FiredCount = 0;
            // Warmup
            for (int i = 0; i < 50; i++) bus.Fire(new BenchPluginSignal { Val = i });

            var sw = Stopwatch.StartNew();
            const int count = 5000;
            for (int i = 0; i < count; i++)
            {
                bus.Fire(new BenchPluginSignal { Val = i });
            }
            sw.Stop();

            Console.WriteLine($"[Nexus Architecture Stress] Plugin Pipeline: {count} dispatches in {sw.ElapsedMilliseconds} ms");
            Report("8. PluginSystem_DecoratorChain_Interceptor_Stress", sw.ElapsedMilliseconds < 500,
                $"elapsed={sw.ElapsedMilliseconds}ms for {count} dispatches");
        }

        // ---------------------------------------------------------------------
        // 9. Netcode Rollback Replay & In-Place Compaction High-Jitter Zero-GC
        // ---------------------------------------------------------------------
        private static void Test_Netcode_Rollback_Replay_HighJitter_ZeroGC()
        {
            var history = new NetworkSignalHistory<NetcodePerfSignal>(2048);
            var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());

            // Warmup
            for (int c = 0; c < 50; c++)
            {
                for (int t = 0; t < 10; t++) history.Add(c * 10 + t, new NetcodePerfSignal(c * 10 + t));
                history.ReplaySignals(c * 10 + 4, bus);
                history.RemoveSignalsAfter(c * 10 + 6);
                history.Prune(c * 10 + 2);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int cycles = 2000;
            for (int c = 50; c < 50 + cycles; c++)
            {
                // Simulate 10 ticks per frame
                for (int t = 0; t < 10; t++) history.Add(c * 10 + t, new NetcodePerfSignal(c * 10 + t));
                // Simulate 6-tick deep rollback replay
                history.ReplaySignals(c * 10 + 4, bus);
                // Simulate compaction
                history.RemoveSignalsAfter(c * 10 + 7);
                // Prune
                history.Prune(c * 10 + 3);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] Netcode High-Jitter Rollback: {allocated} bytes for {cycles} 10-tick cycles");
            Report("9. Netcode_Rollback_Replay_HighJitter_ZeroGC", allocated <= 128,
                $"allocated={allocated} bytes for {cycles} cycles (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 10. ErrorCollection & PerformanceMonitor Multi-Threaded Stress
        // ---------------------------------------------------------------------
        private static void Test_ErrorCollection_And_PerfMonitor_Concurrent_Stress()
        {
            ErrorCollection.Clear();
            var exceptions = new Exception[100];
            for (int i = 0; i < exceptions.Length; i++) exceptions[i] = new InvalidOperationException($"Stress Error {i}");

            const int numThreads = 8;
            const int opsPerThread = 10000;
            var threads = new Thread[numThreads];

            var sw = Stopwatch.StartNew();
            for (int t = 0; t < numThreads; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        ErrorCollection.CollectException(exceptions[i % exceptions.Length]);
                        PerformanceMonitor.RecordMetric("StressMetric", i % 100);
                    }
                });
                threads[t].Start();
            }

            for (int t = 0; t < numThreads; t++) threads[t].Join();
            sw.Stop();

            var recent = ErrorCollection.GetRecentErrors();
            var frequent = ErrorCollection.GetFrequentErrors();

            Console.WriteLine($"[Nexus Architecture Stress] ErrorCollection & PerfMonitor Concurrent Stress: {numThreads * opsPerThread} ops across 8 threads in {sw.ElapsedMilliseconds} ms");
            Report("10. ErrorCollection_And_PerfMonitor_Concurrent_Stress", sw.ElapsedMilliseconds < 1500 && recent.Length > 0,
                $"elapsed={sw.ElapsedMilliseconds}ms for {numThreads * opsPerThread} ops (limit <1500ms)");
        }
    }
}
