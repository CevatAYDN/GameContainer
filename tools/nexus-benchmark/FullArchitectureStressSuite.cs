using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.FSM;
using Nexus.Core.Lifecycle;
using Nexus.Core.Services;
using Nexus.Netcode;
using UnityEngine;

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

    /// <summary>
    /// Scoped cross-context signal routed through the REAL SignalBus path (the former
    /// SignalDispatchPipeline was a divergent orphan copy with case-sensitive == and
    /// Fire() semantics — deleted. This signal exercises SignalBus.BroadcastCrossContext
    /// with the BUG-5 OrdinalIgnoreCase scope match against a differently-cased target).
    /// </summary>
    [CrossContext("Pipeline-Target")]
    public struct PipelineCrossSignal { public int Id; }
    public class PipelineCrossCmd : ICommand<PipelineCrossSignal>
    {
        public static int FiredCount;
        public void Execute(PipelineCrossSignal signal) => Interlocked.Increment(ref FiredCount);
    }

    /// <summary>Scoped to a tag no context has — delivery must silently no-op (no throw).</summary>
    [CrossContext("pipeline-missing")]
    public struct PipelineMissingSignal { public int Id; }
    public class PipelineMissingCmd : ICommand<PipelineMissingSignal>
    {
        public static int FiredCount;
        public void Execute(PipelineMissingSignal signal) => Interlocked.Increment(ref FiredCount);
    }

    internal sealed class TestLifecycle : IContextLifecycle
    {
        public readonly List<string> Log = new List<string>();
        public bool ThrowOnInit;
        public bool ThrowOnStart;
        public void OnConfigure(IContextBuilder builder) { Log.Add("configure"); }
        public ValueTask OnInitializeAsync(CancellationToken ct)
        {
            Log.Add("init");
            if (ThrowOnInit) throw new InvalidOperationException("init-boom");
            return default;
        }
        public ValueTask OnStartAsync(CancellationToken ct)
        {
            Log.Add("start");
            if (ThrowOnStart) throw new InvalidOperationException("start-boom");
            return default;
        }
        public void OnDispose() { Log.Add("dispose"); }
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

    public class LazyTestTarget
    {
        [Inject] public LazyInjection<DependencyLeaf> DeferredLeaf;
    }

    public struct CompSigA { public int Val; }
    public struct CompSigB { public int Val; }

    public class CompCommand : ICompositeCommand
    {
        public static int FiredCount;
        public void Execute(CompositeContext context) => FiredCount++;
    }

    /// <summary>Simple IContextResolver that serves a fixed list of contexts. Zero-alloc after warmup.</summary>
    public class ListContextResolver : IContextResolver
    {
        private readonly IReadOnlyList<IContext> _contexts;
        public ListContextResolver(List<IContext> contexts) => _contexts = contexts;
        public IReadOnlyList<IContext> GetActiveContexts() => _contexts;
    }

    public struct RollbackSignal : INetworkSignal
    {
        public int Health;
    }

    /// <summary>Snapshotable model whose state is driven by RollbackCommand (netcode rollback test).</summary>
    public class RollbackModel : ISnapshotableModel<RollbackSignal>
    {
        public int Health;
        public RollbackSignal CaptureSnapshot() => new RollbackSignal { Health = Health };
        public void RestoreSnapshot(RollbackSignal state) => Health = state.Health;
    }

    public class RollbackCommand : ICommand<RollbackSignal>
    {
        [Inject] public RollbackModel Model;
        public void Execute(RollbackSignal signal) => Model.Health = signal.Health;
    }

    public struct AsyncCmdSignal { public int Val; }

    public class AsyncPerfCommand : IAsyncCommand<AsyncCmdSignal>
    {
        [Inject] public TestCounter Counter;
        public System.Threading.Tasks.ValueTask ExecuteAsync(AsyncCmdSignal signal, CancellationToken ct)
        {
            Counter.Value++;
            return default;
        }
    }

    public class BenchTickable : ITickable
    {
        public int Count;
        public void Tick(float deltaTime) => Count++;
    }

    public class BenchFixedTickable : IFixedTickable
    {
        public int Count;
        public void FixedTick(float fixedDeltaTime) => Count++;
    }

    public class BenchLateTickable : ILateTickable
    {
        public int Count;
        public void LateTick(float deltaTime) => Count++;
    }

    // =========================================================================
    // FULL ARCHITECTURE ADVERSARIAL STRESS SUITE IMPLEMENTATION
    // =========================================================================

    public static class FullArchitectureStressSuite
    {
        private static int _failures = 0;
        private static int _testCount = 0;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Architecture Stress] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("ArchitectureStress", name, ok, detail);
            if (!ok) _failures++;
            _testCount++;
        }

        public static int Run()
        {
            _failures = 0;
            _testCount = 0;
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
            Test_CompositeCommand_Trigger_ZeroGC();
            Test_LazyInjection_ResolveOnce_ZeroGC();
            Test_SignalBus_SubscribeUnsubscribe_Cleanup();
            Test_HybridQueue_NextFrame_ZeroGC();
            Test_Netcode_RollbackAndResimulate_RestoresState();
            Test_AsyncFire_Path_AllDelivered().GetAwaiter().GetResult();
            Test_ObservableProperty_Raise_ZeroGC();
            Test_ObservableList_Mutation_ZeroGC();
            Test_SecureObservable_Write_NoTamper();
            Test_BigDouble_Arithmetic_Correctness();
            Test_TickService_Dispatch_ZeroGC();
            Test_Context_FullLifecycle_AllPhasesOrdered();
            TraceRegistry("after 21");
            Test_Context_AssemblyScan_AutoRegistersSignalHandlers();
            TraceRegistry("after 22");
            Test_Root_Hierarchy_ParentChild_PriorityOrder();
            TraceRegistry("after 23");
            
            Test_View_Mediator_EndToEnd_BindUnbind_PoolReuse();
            TraceRegistry("after 24");
            
            Test_NexusRuntime_Registry_ContextLookup_Metrics();
            TraceRegistry("after 25");
            Test_NetworkMonitor_Events_Latency_Pruning();
            Test_PluginTraceSink_Auth_And_TracingContract();
            Test_EncryptedStorage_RoundTrip_TamperDetection();
            Test_Storage_SaveThrottler_OfflineTime_GameSave();
            Test_ObjectPoolService_SpawnDespawn_Reuse();
            
            Test_Economy_And_Progression_Persistence_Integrity();
            Test_ContextBuilder_Validate_StrictInjection();

            Test_Async_SequentialOrdering_NoOverlap().GetAwaiter().GetResult();
            Test_Async_Timeout_Cancellation().GetAwaiter().GetResult();
            Test_Subscription_AutoDispose_OnContextDispose();
            Test_DoubleDispose_And_FireAfterDispose();
            Test_Dispose_During_Dispatch();
            Test_CrossContext_RealPath_ScopedBroadcast();
            Test_ContextLifecycleOrchestrator_Phases_Isolation();
            TraceRegistry("after 39");
            

            Console.WriteLine("===============================================================================");
            Console.WriteLine(_failures == 0
                ? $"[Nexus Architecture Stress] ALL {_testCount} ARCHITECTURE STRESS TESTS PASSED ✓"
                : $"[Nexus Architecture Stress] {_failures} OF {_testCount} STRESS TEST(S) FAILED ✗");
            Console.WriteLine("===============================================================================");

            // Suite-wide cleanup: real Contexts/NexusRuntime statics, in-memory PlayerPrefs,
            // and temp persistent-data files created by the storage tests.
            NexusRuntime.Reset();
            UnityEngine.PlayerPrefs.ClearAll();
            try { System.IO.Directory.Delete(UnityEngine.Application.persistentDataPath, true); }
            catch { /* already gone */ }

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
            // Warmup — counter is reset AFTER warmup so the assertion below measures
            // exactly the timed dispatch loop (5000 dispatches, not 5100).
            for (int i = 0; i < 100; i++) bus.Fire(new DeepSignal1 { Val = i });
            DeepCommand3.ExecutedCount = 0;

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
            Report("2. SignalBus_Deep_Reentrancy_ZeroGC", allocated <= 128 && DeepCommand3.ExecutedCount == dispatches,
                $"allocated={allocated} bytes for {dispatches} 3-level reentrant dispatches (limit <=128), executed={DeepCommand3.ExecutedCount} (expected exactly {dispatches})");
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
            Report("4. CrossContext_MultiHierarchy_Routing_ZeroGC", allocated <= 128 && CrossCmd.FiredCount == count,
                $"allocated={allocated} bytes for {count} cross-context signals (limit <=128), fired={CrossCmd.FiredCount} (expected exactly {count})");
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

            // Drain with EXACT accounting: count by TotalDrained delta, not by a depth
            // read. Reading ThreadSafeQueueDepth then draining races with concurrent
            // enqueues (items enqueued between the read and the drain are drained but
            // never counted), which can leave the loop spinning forever. TotalDrained
            // is incremented per dequeued item, so the loop provably terminates.
            long drainedTotal = 0;
            var drainWatch = Stopwatch.StartNew();
            while (drainedTotal < numWorkers * enqueuesPerWorker)
            {
                long before = queue.TotalDrained;
                queue.DrainThreadSafe();
                drainedTotal += queue.TotalDrained - before;
                Thread.Sleep(1);
                // Watchdog: never hang a CI run; a stall surfaces as a FAIL instead.
                if (drainWatch.ElapsedMilliseconds > 30000)
                {
                    Console.WriteLine($"[Nexus Architecture Stress] WARNING: drain watchdog hit after 30s (drainedTotal={drainedTotal} of {numWorkers * enqueuesPerWorker}); breaking.");
                    break;
                }
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
            // Use the harness's plugin-aware Context stand-in (NOT MockContext —
            // its RegisterPlugin is a no-op). The stub Context mirrors the real
            // Context: RegisterPlugin wires the plugin's PluginContext into the
            // SignalBus interceptor/decorator gates (HasInterceptors / Plugins.Count),
            // so this test genuinely exercises the pipeline instead of only timing
            // an empty dispatch path.
            var context = new Context();
            context.RegisterPlugin(new SamplePlugin());

            var di = new NexusDI();
            di.Bind<BenchPluginCmd>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, context);

            bus.RegisterCommand(typeof(BenchPluginSignal), typeof(BenchPluginCmd), ExecutionMode.Sequential, 0, false);

            // Warmup
            for (int i = 0; i < 50; i++) bus.Fire(new BenchPluginSignal { Val = i });

            // Reset the pipeline counters AFTER warmup so the assertions below
            // measure exactly the timed dispatch loop.
            SampleInterceptor.InterceptCount = 0;
            SampleDecorator.DecorateCount = 0;
            BenchPluginCmd.FiredCount = 0;

            var sw = Stopwatch.StartNew();
            const int count = 5000;
            for (int i = 0; i < count; i++)
            {
                bus.Fire(new BenchPluginSignal { Val = i });
            }
            sw.Stop();

            context.Dispose();

            bool ok = sw.ElapsedMilliseconds < 500
                && SampleInterceptor.InterceptCount == count
                && SampleDecorator.DecorateCount == count
                && BenchPluginCmd.FiredCount == count;
            Console.WriteLine($"[Nexus Architecture Stress] Plugin Pipeline: {count} dispatches in {sw.ElapsedMilliseconds} ms (intercepts={SampleInterceptor.InterceptCount}, decorates={SampleDecorator.DecorateCount}, fired={BenchPluginCmd.FiredCount})");
            Report("8. PluginSystem_DecoratorChain_Interceptor_Stress", ok,
                $"elapsed={sw.ElapsedMilliseconds}ms for {count} dispatches (limit <500ms), intercepts={SampleInterceptor.InterceptCount} decorates={SampleDecorator.DecorateCount} fired={BenchPluginCmd.FiredCount} (each expected={count})");
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

        // ---------------------------------------------------------------------
        // 11. Composite Command Trigger (re-triggerable) Zero-GC
        // ---------------------------------------------------------------------
        // ---------------------------------------------------------------------
        // 11. Composite Trigger (A+B): functional correctness + measured allocation
        //     NOTE: the composite path is NOT allocation-free by design —
        //     CompositeTriggerState.CapturePayload boxes value-type payloads and
        //     SnapshotPayloads() copies an array per completed trigger. So this
        //     test asserts functional correctness only and REPORTS the allocation
        //     as an informational metric.
        // ---------------------------------------------------------------------
        private static void Test_CompositeCommand_Trigger_ZeroGC()
        {
            var di = new NexusDI();
            di.Bind<CompCommand>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            bus.RegisterCompositeCommand(new[] { typeof(CompSigA), typeof(CompSigB) }, typeof(CompCommand),
                oneShot: false, priority: 0, isAsync: false);

            // Warmup — counter reset AFTER warmup so the assertion measures exactly the timed loop.
            for (int i = 0; i < 100; i++)
            {
                bus.Fire(new CompSigA { Val = i });
                bus.Fire(new CompSigB { Val = i });
            }
            CompCommand.FiredCount = 0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int cycles = 5000;
            for (int i = 0; i < cycles; i++)
            {
                bus.Fire(new CompSigA { Val = i });
                bus.Fire(new CompSigB { Val = i });
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] Composite Trigger (A+B): {allocated} bytes for {cycles} cycles (fired={CompCommand.FiredCount})");
            Report("11. CompositeCommand_Trigger_Correctness", CompCommand.FiredCount == cycles,
                $"fired={CompCommand.FiredCount} (expected exactly {cycles}); informational allocation={allocated} bytes (composite path boxes payloads by design)");
        }

        // ---------------------------------------------------------------------
        // 12. LazyInjection Resolve-Once & Zero-GC Steady State
        // ---------------------------------------------------------------------
        private static void Test_LazyInjection_ResolveOnce_ZeroGC()
        {
            var di = new NexusDI();
            di.Bind<DependencyLeaf>(isSingleton: true);
            var target = new LazyTestTarget();
            di.Inject(target);

            // LazyInjection must resolve once and then return the same instance.
            var first = target.DeferredLeaf.Value;
            var second = target.DeferredLeaf.Value;
            bool resolvedOnce = ReferenceEquals(first, second) && first.Value == 42;

            for (int i = 0; i < 100; i++)
            {
                var _ = target.DeferredLeaf.Value;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int accesses = 5000;
            int sum = 0;
            for (int i = 0; i < accesses; i++)
            {
                sum += target.DeferredLeaf.Value.Value;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] LazyInjection: {allocated} bytes for {accesses} resolved accesses (sum={sum})");
            Report("12. LazyInjection_ResolveOnce_ZeroGC", resolvedOnce && sum == accesses * 42 && allocated <= 128,
                $"sameInstance={resolvedOnce}, sum={sum} expected={accesses * 42}, allocated={allocated} bytes for {accesses} accesses (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 13. SignalBus Subscribe / Unsubscribe Cleanup
        // ---------------------------------------------------------------------
        private static void Test_SignalBus_SubscribeUnsubscribe_Cleanup()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            int received = 0;
            void handler(PerfSignal s) => received++;

            var sub = bus.Subscribe<PerfSignal>(handler);
            bus.Fire(new PerfSignal(1));
            bool delivered = received == 1;

            sub.Dispose();
            bus.Fire(new PerfSignal(2));
            bool removed = received == 1;

            bus.Subscribe<PerfSignal>(handler);
            bus.Fire(new PerfSignal(3));
            bool redelivered = received == 2;

            Report("13. SignalBus_SubscribeUnsubscribe_Cleanup", delivered && removed && redelivered,
                $"delivered={delivered}, removedAfterUnsubscribe={removed}, redeliveredAfterResubscribe={redelivered}, received={received}");
        }

        // ---------------------------------------------------------------------
        // 14. HybridQueue Next-Frame Path Zero-GC
        // ---------------------------------------------------------------------
        private static void Test_HybridQueue_NextFrame_ZeroGC()
        {
            var bus = new SignalBus(new NexusDI(), new CommandPoolManager(new NexusDI()), new MockContext());
            var queue = new HybridQueue(bus);

            for (int b = 0; b < 10; b++)
            {
                for (int i = 0; i < 10; i++) queue.EnqueueNextFrame(new PerfSignal(i));
                queue.DrainNextFrame();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int batches = 500;
            const int perBatch = 10;
            for (int b = 0; b < batches; b++)
            {
                for (int i = 0; i < perBatch; i++) queue.EnqueueNextFrame(new PerfSignal(i));
                queue.DrainNextFrame();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] HybridQueue NextFrame steady-state: {allocated} bytes for {batches * perBatch} enqueues/drains");
            Report("14. HybridQueue_NextFrame_ZeroGC", allocated <= 128, $"allocated={allocated} bytes for {batches * perBatch} ops (limit <=128)");
        }

        // ---------------------------------------------------------------------
        // 15. Netcode Rollback & Resimulate — Model State Restored Correctly
        // ---------------------------------------------------------------------
        private static void Test_Netcode_RollbackAndResimulate_RestoresState()
        {
            var model = new RollbackModel();
            var di = new NexusDI();
            di.BindInstance(model);
            di.Bind<RollbackCommand>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            var networkBus = new NetworkSignalBus(bus);
            networkBus.RegisterModel(model);
            bus.RegisterCommand(typeof(RollbackSignal), typeof(RollbackCommand), ExecutionMode.Sequential, 0, false);

            networkBus.SetTick(0);
            networkBus.Fire(new RollbackSignal { Health = 100 });
            networkBus.SetTick(1);
            networkBus.Fire(new RollbackSignal { Health = 80 });
            networkBus.SetTick(2);
            networkBus.Fire(new RollbackSignal { Health = 30 });

            bool correctAfterFire = model.Health == 30;

            // Full rollback to tick 0, resimulate to tick 2 → replay 100, 80, 30 → 30.
            networkBus.RollbackAndResimulate(rollbackTick: 0, targetTick: 2);
            bool correctAfterFullRollback = model.Health == 30;

            // Partial rollback: resimulate only to tick 1 → replay 100, 80 → 80.
            networkBus.RollbackAndResimulate(rollbackTick: 0, targetTick: 1);
            bool correctAfterPartialRollback = model.Health == 80;

            Console.WriteLine($"[Nexus Architecture Stress] Netcode RollbackAndResimulate: afterFire={correctAfterFire}, afterFullRollback={correctAfterFullRollback}, afterPartialRollback={correctAfterPartialRollback}");
            Report("15. Netcode_RollbackAndResimulate_RestoresState", correctAfterFire && correctAfterFullRollback && correctAfterPartialRollback,
                $"afterFire(30)={correctAfterFire}, afterFullRollback(30)={correctAfterFullRollback}, afterPartialRollback(80)={correctAfterPartialRollback}");
        }

        // ---------------------------------------------------------------------
        // 16. Async Fire Path — All Async Commands Delivered
        // ---------------------------------------------------------------------
        private static async System.Threading.Tasks.ValueTask Test_AsyncFire_Path_AllDelivered()
        {
            var counter = new TestCounter();
            var di = new NexusDI();
            di.BindInstance(counter);
            di.Bind<AsyncPerfCommand>(isSingleton: false);
            var pool = new CommandPoolManager(di);
            var bus = new SignalBus(di, pool, new MockContext());
            bus.RegisterCommand(typeof(AsyncCmdSignal), typeof(AsyncPerfCommand), ExecutionMode.Sequential, 0, true);

            // Warmup (JIT + async state machine + pool)
            for (int i = 0; i < 100; i++)
            {
                await bus.FireAsync(new AsyncCmdSignal { Val = i });
            }
            counter.Value = 0;

            var sw = Stopwatch.StartNew();
            const int count = 5000;
            for (int i = 0; i < count; i++)
            {
                await bus.FireAsync(new AsyncCmdSignal { Val = i });
            }
            sw.Stop();

            double nsPerOp = (sw.ElapsedTicks * 1_000_000_000.0 / Stopwatch.Frequency) / count;
            Console.WriteLine($"[Nexus Architecture Stress] Async Fire: {nsPerOp:F2} ns/op over {count} awaited fires (executed={counter.Value})");
            Report("16. AsyncFire_Path_AllDelivered", counter.Value == count && sw.ElapsedMilliseconds < 2000,
                $"executed={counter.Value} expected={count}, {nsPerOp:F2} ns/op (limit <2s total)");
        }

        // ---------------------------------------------------------------------
        // 17. ObservableProperty Raise Zero-GC (model layer)
        // ---------------------------------------------------------------------
        private static void Test_ObservableProperty_Raise_ZeroGC()
        {
            var prop = new ObservableProperty<int>(0);
            int received = 0;
            int lastOld = -1;
            int lastNew = -1;
            prop.OnChanged((o, n) => { received++; lastOld = o; lastNew = n; });

            for (int i = 0; i < 100; i++) prop.Value = i + 1;
            received = 0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int sets = 5000;
            for (int i = 0; i < sets; i++) prop.Value = i + 101;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] ObservableProperty raise: {allocated} bytes for {sets} notify-sets (received={received})");
            Report("17. ObservableProperty_Raise_ZeroGC", allocated <= 128 && received == sets && lastOld == 100 + sets - 1 && lastNew == 100 + sets,
                $"allocated={allocated} bytes for {sets} notify-sets (limit <=128), received={received}, lastOld={lastOld} lastNew={lastNew}");
        }

        // ---------------------------------------------------------------------
        // 17b. ObservableList Mutation Zero-GC (cached snapshot steady state)
        // ---------------------------------------------------------------------
        private static void Test_ObservableList_Mutation_ZeroGC()
        {
            var list = new ObservableList<int>();
            int added = 0;
            int removed = 0;
            int cleared = 0;
            list.OnAdded((i, v) => added++);
            list.OnRemoved((i, v) => removed++);
            list.OnCleared(() => cleared++);

            // Warm up: build each channel's snapshot cache and grow the backing
            // capacity beyond the measured loop, so the hot path only reuses
            // cached arrays (GetSnapshot returns the cached copy when not dirty).
            for (int i = 0; i < 1000; i++) list.Add(i);
            list.Clear();
            added = 0;
            removed = 0;
            cleared = 0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int cycles = 5000;
            for (int i = 0; i < cycles; i++)
            {
                list.Add(i);
                list.Remove(i);
            }
            list.Clear();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Architecture Stress] ObservableList mutation: {allocated} bytes for {cycles} add/remove cycles + clear (added={added}, removed={removed}, cleared={cleared})");
            Report("17b. ObservableList_Mutation_ZeroGC", allocated <= 128 && added == cycles && removed == cycles && cleared == 1,
                $"allocated={allocated} bytes for {cycles} cycles (limit <=128), added={added}, removed={removed}, cleared={cleared}");
        }

        // ---------------------------------------------------------------------
        // 18. SecureObservable Write Integrity & Notification
        // ---------------------------------------------------------------------
        private static void Test_SecureObservable_Write_NoTamper()
        {
            var secure = new SecureObservableInt(42);
            int received = 0;
            secure.OnChanged((o, n) => received++);

            bool integrityOk = true;
            for (int i = 0; i < 10000; i++)
            {
                secure.Value = i;
                if (secure.Value != i)
                {
                    integrityOk = false;
                    break;
                }
            }
            bool notifyOk = received == 10000;

            Console.WriteLine($"[Nexus Architecture Stress] SecureObservableInt: integrity={integrityOk}, notifications={received}");
            Report("18. SecureObservable_Write_NoTamper", integrityOk && notifyOk,
                $"integrity={integrityOk}, notifications={received} (expected 10000)");
        }

        // ---------------------------------------------------------------------
        // 19. BigDouble Arithmetic Correctness (data layer)
        // ---------------------------------------------------------------------
        private static void Test_BigDouble_Arithmetic_Correctness()
        {
            BigDouble a = 1e100;
            BigDouble b = 1e100;
            BigDouble c = 3.0;

            bool add = (a + b).CompareTo((BigDouble)2e100) == 0;
            bool subZero = (a - a).CompareTo(BigDouble.Zero) == 0;
            bool mulIdentity = (a * BigDouble.One).CompareTo(a) == 0;
            bool divIdentity = (a / a).CompareTo(BigDouble.One) == 0;
            bool mulZero = (a * BigDouble.Zero).CompareTo(BigDouble.Zero) == 0;
            bool commut = (a * c).CompareTo(c * a) == 0;
            bool assoc = ((a + b) + c).CompareTo(a + (b + c)) == 0;
            bool negate = (-a + a).CompareTo(BigDouble.Zero) == 0;
            bool ordering = new BigDouble(1).CompareTo(new BigDouble(2)) < 0
                && new BigDouble(2).CompareTo((BigDouble)1e100) < 0;
            bool intExact = ((BigDouble)12345 + (BigDouble)67890).CompareTo((BigDouble)80235) == 0;

            bool ok = add && subZero && mulIdentity && divIdentity && mulZero
                && commut && assoc && negate && ordering && intExact;

            Console.WriteLine($"[Nexus Architecture Stress] BigDouble: add={add} subZero={subZero} mulIdentity={mulIdentity} divIdentity={divIdentity} mulZero={mulZero} commut={commut} assoc={assoc} negate={negate} ordering={ordering} intExact={intExact}");
            Report("19. BigDouble_Arithmetic_Correctness", ok,
                $"add={add}, subZero={subZero}, mulIdentity={mulIdentity}, divIdentity={divIdentity}, mulZero={mulZero}, commut={commut}, assoc={assoc}, negate={negate}, ordering={ordering}, intExact={intExact}");
        }

        // ---------------------------------------------------------------------
        // 20. TickService Dispatch Zero-GC (300 tickables x 10,000 frames)
        // ---------------------------------------------------------------------
        private static void Test_TickService_Dispatch_ZeroGC()
        {
            var ticks = new BenchTickable[100];
            var fixedTicks = new BenchFixedTickable[100];
            var lateTicks = new BenchLateTickable[100];
            for (int i = 0; i < ticks.Length; i++) ticks[i] = new BenchTickable();
            for (int i = 0; i < fixedTicks.Length; i++) fixedTicks[i] = new BenchFixedTickable();
            for (int i = 0; i < lateTicks.Length; i++) lateTicks[i] = new BenchLateTickable();

            var service = new TickService();
            for (int i = 0; i < ticks.Length; i++) service.RegisterTickable(ticks[i]);
            for (int i = 0; i < fixedTicks.Length; i++) service.RegisterFixedTickable(fixedTicks[i]);
            for (int i = 0; i < lateTicks.Length; i++) service.RegisterLateTickable(lateTicks[i]);

            // Warmup — snapshot arrays built at registration are cached after the first frame.
            for (int i = 0; i < 100; i++)
            {
                service.OnTick(0.016f);
                service.OnFixedTick(0.02f);
                service.OnLateTick(0.016f);
            }
            for (int i = 0; i < ticks.Length; i++) ticks[i].Count = 0;
            for (int i = 0; i < fixedTicks.Length; i++) fixedTicks[i].Count = 0;
            for (int i = 0; i < lateTicks.Length; i++) lateTicks[i].Count = 0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            const int frames = 10000;
            for (int i = 0; i < frames; i++)
            {
                service.OnTick(0.016f);
                service.OnFixedTick(0.02f);
                service.OnLateTick(0.016f);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            bool allTicked = true;
            for (int i = 0; i < ticks.Length && allTicked; i++) allTicked = ticks[i].Count == frames;
            for (int i = 0; i < fixedTicks.Length && allTicked; i++) allTicked = fixedTicks[i].Count == frames;
            for (int i = 0; i < lateTicks.Length && allTicked; i++) allTicked = lateTicks[i].Count == frames;

            Console.WriteLine($"[Nexus Architecture Stress] TickService: {allocated} bytes for {frames} frames x 300 tickables (allTicked={allTicked})");
            Report("20. TickService_Dispatch_ZeroGC", allTicked && allocated <= 128,
                $"allocated={allocated} bytes for {frames} frames x 300 tickables (limit <=128), allTicked={allTicked}");
        }

        // =========================================================================
        // 21–32: INTEGRATION PROOF TESTS (real Context/Root/Runtime/ViewBinder/etc.)
        // =========================================================================

        // ── Shared helpers ──────────────────────────────────────────────────────

        private static void InvokePrivate(object target, string methodName)
        {
            var t = target.GetType();
            MethodInfo m = null;
            while (t != null && m == null)
            {
                m = t.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
                t = t.BaseType;
            }
            if (m != null) m.Invoke(target, null);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(target, value);
        }

        private static ViewBinder GetViewBinder(Context ctx)
        {
            return (ViewBinder)typeof(Context).GetField("_viewBinder", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ctx);
        }

        /// <summary>
        /// Stands in for Unity's main-thread SynchronizationContext so async void
        /// Root.Start() continuations resume on the pump (test) thread — the exact
        /// condition the real Root main-thread guard requires.
        /// </summary>
        private sealed class TestSyncContext : SynchronizationContext
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback cb, object state)> _queue =
                new();

            public override void Post(SendOrPostCallback d, object state) => _queue.Enqueue((d, state));
            public override void Send(SendOrPostCallback d, object state) => d(state);

            public int QueueDepth => _queue.Count;

            /// <summary>
            /// One pump = one frame: executes a single queued continuation so that
            /// Task.Yield() loops (Root's parent/sibling waits) consume real frame
            /// counts, mirroring Unity's per-frame queue draining.
            /// </summary>
            public void Pump()
            {
                if (_queue.TryDequeue(out var item)) item.cb(item.state);
            }
        }

        private static bool PumpUntil(Func<bool> condition, TestSyncContext syncCtx, int maxIterations)
        {
            int it = 0;
            while (!condition() && it++ < maxIterations)
            {
                syncCtx.Pump();
                if (condition()) break;
                Thread.Sleep(1);
            }
            return condition();
        }

        // ── 21+ helper types ────────────────────────────────────────────────────

        private static void TraceRegistry(string point)
        {
            Console.WriteLine($"[Nexus Architecture Stress]   [reg] {point}: activeContexts={NexusRuntime.ActiveContexts.Count}");
        }

        private sealed class CaptureLogger : Nexus.Core.Services.ILoggerService
        {
            public readonly List<string> Errors = new();
            public bool IsEnabled { get; set; } = true;
            public void Log(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) => Errors.Add(message);
            public void LogException(Exception exception) => Errors.Add(exception?.ToString() ?? "null");
        }

        private sealed class LifecycleRecorder : IContextLifecycle
        {
            public readonly List<string> Log;
            public Context Ctx;
            public LifecycleRecorder(List<string> log) { Log = log; }

            public void OnConfigure(IContextBuilder builder)
            {
                Log.Add("configure");
                builder.BindService<TestInitService>();
                builder.BindReactiveModel<TestReactiveModel>();
                builder.BindLazyService<TestLazyService>();
                builder.BindCommand<PerfSignal, PerfCommand>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) { Log.Add("init"); return default; }

            public ValueTask OnStartAsync(CancellationToken ct)
            {
                Log.Add("start");
                // The documented lazy mechanism: LazyInjection<T>.Value resolves the service
                // on first access and enqueues it for the second lazy-init drain, which runs
                // after OnStartAsync. Direct TryResolve does NOT trigger deferred init.
                var host = new LazyHost();
                Ctx.Container.Inject(host);
                _ = host.Lazy.Value;
                return default;
            }

            public void OnDispose() { Log.Add("dispose"); }
        }

        private sealed class LazyHost
        {
#pragma warning disable 0649 // assigned via DI injection at resolve time
            [Inject] public LazyInjection<TestLazyService> Lazy;
#pragma warning restore 0649
        }

        private sealed class TestInitService : INexusService
        {
            public static List<string> Log;
            public ValueTask InitializeAsync(CancellationToken ct) { Log?.Add("serviceInit"); return default; }
            public void OnDispose() { Log?.Add("serviceDispose"); }
        }

        private sealed class TestReactiveModel : IReactiveModel
        {
            public static List<string> Log;
            public ValueTask OnBind(CancellationToken ct) { Log?.Add("modelBind"); return default; }
        }

        private sealed class TestLazyService : INexusService
        {
            public static List<string> Log;
            public static bool Initialized;
            public ValueTask InitializeAsync(CancellationToken ct) { Initialized = true; Log?.Add("lazyInit"); return default; }
            public void OnDispose() { }
        }

        private struct ScanSignal { public int Val; }
        private struct ScanSigA { public int Val; }
        private struct ScanSigB { public int Val; }

        [SignalHandler(typeof(ScanSignal))]
        private sealed class ScanCommand : ICommand<ScanSignal>
        {
            public static int FiredCount;
            public void Execute(ScanSignal signal) => FiredCount++;
        }

        [CompositeSignalHandler(typeof(ScanSigA), typeof(ScanSigB))]
        private sealed class ScanCompositeCommand : ICompositeCommand
        {
            public static int FiredCount;
            public void Execute(CompositeContext signals)
            {
                if (signals.TryGet<ScanSigA>(out _) && signals.TryGet<ScanSigB>(out _)) FiredCount++;
            }
        }

        private sealed class RootLifecycleRecorder : MonoBehaviour, IContextLifecycle
        {
            public readonly List<string> Log = new();
            public readonly CaptureLogger Logger = new();
            public bool DelayInitialize;
            public string StartLabel = "rootStart";

            public void OnConfigure(IContextBuilder builder) => builder.BindInstance<ILoggerService>(Logger);

            public async ValueTask OnInitializeAsync(CancellationToken ct)
            {
                if (DelayInitialize)
                {
                    await Task.Delay(150, ct);
                    Log.Add("rootInitDone");
                }
            }

            public ValueTask OnStartAsync(CancellationToken ct) { Log.Add(StartLabel); return default; }
            public void OnDispose() { }
        }

        private struct TestViewSignal { public int Val; }

        [Mediator(typeof(TestMediator))]
        private sealed class TestView : View
        {
            public int BoundCount;
            protected override void OnBind(IContext context) { BoundCount++; }
            protected override void OnUnbind() { BoundCount--; }
        }

        private sealed class TestMediator : Mediator<TestView>
        {
            public int Received;
            protected override void OnBind() { Subscribe<TestViewSignal>(s => Received++); }
            protected override void OnReset() { Received = 0; }
        }

        private sealed class CountingTraceSink : INexusTraceSink
        {
            public int Written;
            public void Write(in TraceEvent traceEvent) => Written++;
        }

        private sealed class TraceProviderPlugin : INexusPlugin
        {
            public readonly CountingTraceSink Sink = new();
            public int RegisteredCalls;

            public NexusPluginManifest Manifest { get; } =
                new("TraceProbe", "1.0.0", PluginCapabilities.TraceProvider);

            public void OnPluginRegistered(IPluginContext context)
            {
                RegisteredCalls++;
                context.RegisterTraceSink(Sink);
            }
            public void OnPluginRemoved() { }
        }

        private sealed class NoCapabilityPlugin : INexusPlugin
        {
            public NexusPluginManifest Manifest { get; } = new("NoCap", "1.0.0", PluginCapabilities.None);
            public void OnPluginRegistered(IPluginContext context) { }
            public void OnPluginRemoved() { }
        }

        // ── 33–37: async/ordering/cancellation/teardown proof types ────────────

        public struct AsyncOrderSignal { public int Id; }
        public class AsyncOrderCommandA : IAsyncCommand<AsyncOrderSignal>
        {
            public static TaskCompletionSource<bool> Gate;
            public static readonly List<string> Log = new();
            public static int ConcurrentB;
            public async ValueTask ExecuteAsync(AsyncOrderSignal signal, CancellationToken ct)
            {
                Log.Add($"A-start:{signal.Id}");
                await Gate.Task;
                Log.Add($"A-end:{signal.Id}");
            }
        }
        public class AsyncOrderCommandB : IAsyncCommand<AsyncOrderSignal>
        {
            public async ValueTask ExecuteAsync(AsyncOrderSignal signal, CancellationToken ct)
            {
                AsyncOrderCommandA.Log.Add($"B-start:{signal.Id}");
                if (AsyncOrderCommandA.Log.IndexOf($"A-end:{signal.Id}") < 0) AsyncOrderCommandA.ConcurrentB++;
                await Task.Yield();
                AsyncOrderCommandA.Log.Add($"B-end:{signal.Id}");
            }
        }

        public struct SlowAsyncSignal { public int Val; }
        public class SlowAsyncCommand : IAsyncCommand<SlowAsyncSignal>
        {
            public static int Started;
            public static bool Cancelled;
            public async ValueTask ExecuteAsync(SlowAsyncSignal signal, CancellationToken ct)
            {
                Interlocked.Increment(ref Started);
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
        }

        public struct SubLifecycleSignal { public int Val; }
        public struct SelfDisposeSignal { public int Val; }
        public class SelfDisposeCommand : ICommand<SelfDisposeSignal>
        {
            public static Context Ctx;
            public static int Executed;
            public void Execute(SelfDisposeSignal signal)
            {
                Executed++;
                if (Ctx != null) { Ctx.Dispose(); Ctx = null; }
            }
        }

        private sealed class FakePlayerPrefsService : IPlayerPrefsService
        {
            private readonly Dictionary<string, string> _store = new();
            public int GetInt(string key, int defaultValue = 0) => int.TryParse(GetString(key, null), out int r) ? r : defaultValue;
            public void SetInt(string key, int value) => SetString(key, value.ToString());
            public bool GetBool(string key, bool defaultValue = false) => bool.TryParse(GetString(key, null), out bool r) ? r : defaultValue;
            public void SetBool(string key, bool value) => SetString(key, value.ToString());
            public string GetString(string key, string defaultValue = "") => _store.TryGetValue(key, out var v) ? v : defaultValue;
            public void SetString(string key, string value) => _store[key] = value;
            public float GetFloat(string key, float defaultValue = 0f) => float.TryParse(GetString(key, null), out float r) ? r : defaultValue;
            public void SetFloat(string key, float value) => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            public long GetLong(string key, long defaultValue = 0L) => long.TryParse(GetString(key, null), out long r) ? r : defaultValue;
            public void SetLong(string key, long value) => SetString(key, value.ToString());
            public bool HasKey(string key) => _store.ContainsKey(key);
            public void DeleteKey(string key) => _store.Remove(key);
            public void Save() { }
        }

        private sealed class FakeTimeProvider : ITimeProvider
        {
            public float Now { get; set; }
        }

        private sealed class TestPoolable : Component, IPoolable
        {
            public int SpawnCount;
            public int DespawnCount;
            public void OnSpawned() => SpawnCount++;
            public void OnDespawned() => DespawnCount++;
        }

        private sealed class TestSaveModel : ISaveDataProvider
        {
            public string Data = "v1";
            public byte[] CaptureSaveData() => Encoding.UTF8.GetBytes(Data);
            public void RestoreSaveData(byte[] data) => Data = Encoding.UTF8.GetString(data);
        }

        // ── 33–37: async ordering, cancellation, teardown proof ────────────────

        private static async Task Test_Async_SequentialOrdering_NoOverlap()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                AsyncOrderCommandA.Log.Clear();
                AsyncOrderCommandA.ConcurrentB = 0;
                AsyncOrderCommandA.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Register B (priority 0) FIRST, then A (priority 1): the async path must
                // still run A before B (priority order) and await each handler fully
                // (sequential guarantee — B must not start while A is suspended on the gate).
                ctx.Resolve<SignalBus>().RegisterCommand(typeof(AsyncOrderSignal), typeof(AsyncOrderCommandB), ExecutionMode.Sequential, 0, isAsync: true);
                ctx.Resolve<SignalBus>().RegisterCommand(typeof(AsyncOrderSignal), typeof(AsyncOrderCommandA), ExecutionMode.Sequential, 1, isAsync: true);

                var fire = ctx.SignalBus.FireAsync(new AsyncOrderSignal { Id = 1 }).AsTask();
                await Task.Delay(50); // let A reach the gate
                bool aStartedFirst = AsyncOrderCommandA.Log.Count == 1
                    && AsyncOrderCommandA.Log[0] == "A-start:1"
                    && AsyncOrderCommandA.Log.Count == 1;

                AsyncOrderCommandA.Gate.SetResult(true);
                var completed = await Task.WhenAny(fire, Task.Delay(5000));
                bool finishedInTime = completed == fire;
                await fire;

                string order = string.Join(",", AsyncOrderCommandA.Log);
                bool sequential = order == "A-start:1,A-end:1,B-start:1,B-end:1";
                bool noOverlap = AsyncOrderCommandA.ConcurrentB == 0;
                ok = aStartedFirst && finishedInTime && sequential && noOverlap;
                detail = $"aStartedFirst={aStartedFirst} finishedInTime={finishedInTime} sequential={sequential} " +
                    $"noOverlap={noOverlap} order=[{order}]";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                AsyncOrderCommandA.Gate?.TrySetResult(true);
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Async ordering: {detail}");
            Report("33. FireAsync_SequentialOrdering_NoOverlap", ok, detail);
        }

        private static async Task Test_Async_Timeout_Cancellation()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                SlowAsyncCommand.Started = 0;
                SlowAsyncCommand.Cancelled = false;
                ctx.Resolve<SignalBus>().RegisterCommand(typeof(SlowAsyncSignal), typeof(SlowAsyncCommand), ExecutionMode.Sequential, 0, isAsync: true);

                bool timedOut = false;
                try
                {
                    await ctx.SignalBus.FireAsyncWithTimeout(new SlowAsyncSignal { Val = 1 }, 100);
                }
                catch (OperationCanceledException) { timedOut = true; }

                // The bus must survive the timeout and keep working.
                int received = 0;
                var sub = ctx.SignalBus.Subscribe<PerfSignal>(_ => received++);
                ctx.SignalBus.Fire(new PerfSignal(0));
                sub.Dispose();

                ok = timedOut && SlowAsyncCommand.Started == 1 && SlowAsyncCommand.Cancelled && received == 1;
                detail = $"timedOut={timedOut} started={SlowAsyncCommand.Started} cancelled={SlowAsyncCommand.Cancelled} busAlive={received == 1}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Async timeout/cancel: {detail}");
            Report("34. FireAsyncWithTimeout_Cancellation_BusSurvives", ok, detail);
        }

        private static void Test_Subscription_AutoDispose_OnContextDispose()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                int received = 0;
                ctx.SignalBus.Subscribe<SubLifecycleSignal>(_ => received++);
                ctx.SignalBus.Fire(new SubLifecycleSignal { Val = 1 });
                bool active = received == 1;

                ctx.Dispose();
                ctx.SignalBus.Fire(new SubLifecycleSignal { Val = 2 });
                bool silent = received == 1; // token-triggered auto-dispose killed the subscription

                ok = active && silent && NexusRuntime.ActiveContexts.Count == 0;
                detail = $"receivedBeforeDispose={received - (silent ? 0 : 1)} active={active} silent={silent} registeredAfterDispose={NexusRuntime.ActiveContexts.Count}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Subscription auto-dispose: {detail}");
            Report("35. Subscription_AutoDispose_OnContextDispose", ok, detail);
        }

        private static void Test_DoubleDispose_And_FireAfterDispose()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                bool noThrow = true;
                ctx.Dispose();
                try { ctx.Dispose(); } catch { noThrow = false; } // idempotent

                try { ctx.SignalBus.Fire(new PerfSignal(1)); } catch { noThrow = false; } // safe no-op
                var unregistered = NexusRuntime.ActiveContexts.Count == 0;

                ok = noThrow && unregistered;
                detail = $"noThrow={noThrow} unregistered={unregistered}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Double dispose: {detail}");
            Report("36. Context_DoubleDispose_And_FireAfterDispose", ok, detail);
        }

        private static void Test_Dispose_During_Dispatch()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            string detail;
            try
            {
                SelfDisposeCommand.Ctx = ctx;
                SelfDisposeCommand.Executed = 0;
                ctx.Resolve<SignalBus>().RegisterCommand(typeof(SelfDisposeSignal), typeof(SelfDisposeCommand), ExecutionMode.Sequential, 0, false);

                bool noThrow = true;
                try { ctx.SignalBus.Fire(new SelfDisposeSignal { Val = 1 }); } catch { noThrow = false; }
                bool executed = SelfDisposeCommand.Executed == 1;

                try { ctx.SignalBus.Fire(new SelfDisposeSignal { Val = 2 }); } catch { noThrow = false; } // no-op after dispose
                bool stopped = SelfDisposeCommand.Executed == 1;

                ok = noThrow && executed && stopped && NexusRuntime.ActiveContexts.Count == 0;
                detail = $"noThrow={noThrow} executed={executed} stopped={stopped} registeredAfterDispose={NexusRuntime.ActiveContexts.Count}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                SelfDisposeCommand.Ctx = null;
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Dispose-during-dispatch: {detail}");
            Report("37. Dispose_During_Dispatch", ok, detail);
        }

        // ── 38. Real cross-context broadcast through SignalBus (BUG-5 OrdinalIgnoreCase) ──
        // The former SignalDispatchPipeline was a divergent orphan copy (case-sensitive ==,
        // Fire() instead of FireCrossContext, no scope-less broadcast). Deleted; this test
        // now covers the REAL SignalBus.BroadcastCrossContext path incl. the BUG-5
        // case-insensitive scope match. (The scope-less broadcast-to-all branch is covered
        // separately by test 4.)

        private static void Test_CrossContext_RealPath_ScopedBroadcast()
        {
            NexusRuntime.Reset();

            // Source (fires) + target (receives). Target scope differs in case from the
            // [CrossContext("Pipeline-Target")] attribute → must match OrdinalIgnoreCase.
            var di = new NexusDI();
            var pool = new CommandPoolManager(di);
            var sourceCtx = new MockContext { ScopeTag = "source" };
            var targetCtx = new MockContext { ScopeTag = "pipeline-target" };
            var resolver = new ListContextResolver(new List<IContext> { sourceCtx, targetCtx });
            var sourceBus = new SignalBus(di, pool, sourceCtx, resolver);
            sourceCtx.SignalBus = sourceBus;
            var targetDi = new NexusDI();
            var targetPool = new CommandPoolManager(targetDi);
            var targetBus = new SignalBus(targetDi, targetPool, targetCtx, resolver);
            targetCtx.SignalBus = targetBus;

            targetDi.Bind<PipelineCrossCmd>(isSingleton: false);
            targetDi.Bind<PipelineMissingCmd>(isSingleton: false);
            targetBus.RegisterCommand(typeof(PipelineCrossSignal), typeof(PipelineCrossCmd), ExecutionMode.Sequential, 0, false);
            targetBus.RegisterCommand(typeof(PipelineMissingSignal), typeof(PipelineMissingCmd), ExecutionMode.Sequential, 0, false);

            // 1. Scoped broadcast: attribute scope "Pipeline-Target" vs target "pipeline-target" →
            //    delivered via the REAL path (BUG-5 fix would previously skip case-mismatched scopes).
            PipelineCrossCmd.FiredCount = 0;
            PipelineMissingCmd.FiredCount = 0;
            sourceBus.Fire(new PipelineCrossSignal { Id = 7 });
            bool delivered = PipelineCrossCmd.FiredCount == 1;

            // 2. Scoped signal with a tag no context owns → no throw, no delivery.
            bool missingNoThrow = true;
            try { sourceBus.Fire(new PipelineMissingSignal { Id = 8 }); }
            catch { missingNoThrow = false; }
            bool missingStillZero = PipelineMissingCmd.FiredCount == 0 && PipelineCrossCmd.FiredCount == 1;

            // 3. Default resolver (null contextResolver → NexusRuntime.DefaultContextResolver)
            //    with no active contexts → broadcast over empty set, no throw.
            bool fallbackNoThrow = true;
            try { new SignalBus(di, pool, new MockContext { ScopeTag = "bare" }).Fire(new PipelineCrossSignal { Id = 9 }); }
            catch { fallbackNoThrow = false; }

            bool ok = delivered && missingNoThrow && missingStillZero && fallbackNoThrow;
            Report("38. CrossContext_RealPath_ScopedBroadcast", ok,
                $"scoped-case-insensitive=delivered({delivered}), missing-tag=no-throw({missingNoThrow}) no-delivery({missingStillZero}), default-resolver=no-throw({fallbackNoThrow})");
        }

        // ── 39. ContextLifecycleOrchestrator phase ordering + isolation + cancel ──

        private static void Test_ContextLifecycleOrchestrator_Phases_Isolation()
        {
            var orchestrator = new ContextLifecycleOrchestrator();

            var a = new TestLifecycle();
            var b = new TestLifecycle { ThrowOnInit = true };
            var c = new TestLifecycle { ThrowOnStart = true };
            orchestrator.ExecuteLifecyclePhasesAsync(new List<IContextLifecycle> { a, b, c }, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

            // All inits run first (b's init exception isolated), then all starts (c's start exception isolated).
            bool orderOk = string.Join(",", a.Log) == "init,start"
                        && string.Join(",", b.Log) == "init,start"
                        && string.Join(",", c.Log) == "init,start";

            var cancelled = new CancellationToken(canceled: true);
            var d = new TestLifecycle();
            orchestrator.ExecuteLifecyclePhasesAsync(new List<IContextLifecycle> { d }, cancelled)
                .AsTask().GetAwaiter().GetResult();
            bool cancelOk = d.Log.Count == 0;

            bool emptyNoThrow = true;
            try
            {
                orchestrator.ExecuteLifecyclePhasesAsync(null, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                orchestrator.ExecuteLifecyclePhasesAsync(new List<IContextLifecycle>(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch { emptyNoThrow = false; }

            bool ok = orderOk && cancelOk && emptyNoThrow;
            Report("39. ContextLifecycleOrchestrator_Phases_Isolation", ok,
                $"phases=all-init-then-all-start ({orderOk}), exception-isolated, pre-cancelled=zero-calls({cancelOk}), null/empty=no-throw({emptyNoThrow})");
        }

        // ── 21. Context factory full lifecycle ──────────────────────────────────

        private static void Test_Context_FullLifecycle_AllPhasesOrdered()
        {
            var log = new List<string>();
            TestInitService.Log = log;
            TestReactiveModel.Log = log;
            TestLazyService.Log = log;
            TestLazyService.Initialized = false;

            var ctx = ContextFactory.Create();
            bool ok = false;
            bool disposed = false;
            try
            {
                var lifecycle = new LifecycleRecorder(log) { Ctx = ctx };
                ctx.Configure(new[] { lifecycle });

                var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
                vt.GetAwaiter().GetResult();

                ctx.Dispose();
                disposed = true;

                var expected = new[] { "configure", "modelBind", "serviceInit", "init", "start", "lazyInit", "dispose", "serviceDispose" };
                ok = log.Count == expected.Length;
                if (ok)
                {
                    for (int i = 0; i < expected.Length; i++)
                    {
                        if (log[i] != expected[i]) { ok = false; break; }
                    }
                }
                ok = ok && TestLazyService.Initialized;
            }
            finally
            {
                if (!disposed) ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Context lifecycle order: {string.Join(" -> ", log)}");
            Report("21. Context_FullLifecycle_AllPhasesOrdered", ok,
                $"order={string.Join(",", log)} (expected configure,modelBind,serviceInit,init,start,lazyInit,dispose,serviceDispose)");
        }

        // ── 22. Assembly scan auto-registration ─────────────────────────────────

        private static void Test_Context_AssemblyScan_AutoRegistersSignalHandlers()
        {
            ScanCommand.FiredCount = 0;
            ScanCompositeCommand.FiredCount = 0;

            var ctx = ContextFactory.Create();
            bool ok = false;
            try
            {
                ctx.Configure();

                // The [SignalHandler]/[CompositeSignalHandler] attributes on ScanCommand /
                // ScanCompositeCommand must be picked up by the real assembly scan and
                // auto-registered — no manual RegisterCommand calls here.
                ctx.SignalBus.Fire(new ScanSignal { Val = 1 });
                ctx.SignalBus.Fire(new ScanSigA { Val = 2 });
                ctx.SignalBus.Fire(new ScanSigB { Val = 3 });

                ok = ScanCommand.FiredCount == 1 && ScanCompositeCommand.FiredCount == 1;
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Assembly scan: commandFired={ScanCommand.FiredCount} compositeFired={ScanCompositeCommand.FiredCount}");
            Report("22. Context_AssemblyScan_AutoRegistersSignalHandlers", ok,
                $"auto-registered [SignalHandler] command fired={ScanCommand.FiredCount} (expected 1), [CompositeSignalHandler] composite fired={ScanCompositeCommand.FiredCount} (expected 1)");
        }

        // ── 23. Root hierarchy: parent-child + sibling priority ─────────────────

        private static void Test_Root_Hierarchy_ParentChild_PriorityOrder()
        {
            var prevCtx = SynchronizationContext.Current;
            var syncCtx = new TestSyncContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            bool ok = false;
            string detail = "";
            var parentGo = new GameObject("ParentRoot");
            var childAGroup = new GameObject("ChildAHierarchy");
            var childBGroup = new GameObject("ChildBHierarchy");
            try
            {
                var parent = parentGo.AddComponent<Root>();
                var parentLifecycle = parentGo.AddComponent<RootLifecycleRecorder>();
                parentLifecycle.DelayInitialize = true; // child must WAIT for this to finish
                parentLifecycle.StartLabel = "parentStart";

                var childA = childAGroup.AddComponent<Root>();
                SetPrivateField(childA, "parentRoot", parent);
                SetPrivateField(childA, "initializationPriority", 10);
                var childALifecycle = childAGroup.AddComponent<RootLifecycleRecorder>();
                childALifecycle.StartLabel = "childAStart";

                var childB = childBGroup.AddComponent<Root>();
                SetPrivateField(childB, "parentRoot", parent);
                SetPrivateField(childB, "initializationPriority", 5);
                var childBLifecycle = childBGroup.AddComponent<RootLifecycleRecorder>();
                childBLifecycle.StartLabel = "childBStart";

                InvokePrivate(parent, "Awake");
                int ctxAfterParentAwake = NexusRuntime.ActiveContexts.Count;
                InvokePrivate(childA, "Awake");
                int ctxAfterChildAAwake = NexusRuntime.ActiveContexts.Count;
                InvokePrivate(childB, "Awake");
                int ctxAfterChildBAwake = NexusRuntime.ActiveContexts.Count;

                InvokePrivate(parent, "Start");
                InvokePrivate(childA, "Start");
                InvokePrivate(childB, "Start");

                bool done = PumpUntil(() => parent.IsInitialized && childA.IsInitialized && childB.IsInitialized,
                    syncCtx, 20000);
                int ctxAfterPump = NexusRuntime.ActiveContexts.Count;
                int queueAfterPump = syncCtx.QueueDepth;

                string rootErrors = "";
                foreach (var logger in new[] { parentLifecycle.Logger, childALifecycle.Logger, childBLifecycle.Logger })
                {
                    if (logger.Errors.Count > 0) rootErrors += $"[{string.Join(" | ", logger.Errors)}]";
                }

                var log = new List<string>();
                log.AddRange(parentLifecycle.Log);
                log.AddRange(childALifecycle.Log);
                log.AddRange(childBLifecycle.Log);

                int idxParentInit = log.IndexOf("rootInitDone");
                int idxParentStart = log.IndexOf("parentStart");
                int idxAStart = log.IndexOf("childAStart");
                int idxBStart = log.IndexOf("childBStart");

                ok = done
                    && idxParentInit >= 0 && idxParentStart >= 0 && idxAStart >= 0 && idxBStart >= 0
                    && idxParentInit < idxParentStart
                    && idxParentStart < idxAStart
                    && idxAStart < idxBStart;

                detail = $"initialized={parent.IsInitialized}/{childA.IsInitialized}/{childB.IsInitialized} " +
                    $"ctxCreated={ctxAfterParentAwake}/{ctxAfterChildAAwake}/{ctxAfterChildBAwake} " +
                    $"ctxAfterPump={ctxAfterPump} queueAfterPump={queueAfterPump} order: {string.Join(",", log)} " +
                    $"childAContextGone={childA.Context == null} childBContextGone={childB.Context == null}";
                if (rootErrors.Length > 0) detail += $" ROOT_ERRORS: {rootErrors}";

                InvokePrivate(parent, "OnDestroy");
                InvokePrivate(childA, "OnDestroy");
                InvokePrivate(childB, "OnDestroy");

                int registeredAfterDestroy = NexusRuntime.ActiveContexts.Count;
                ok = ok && registeredAfterDestroy == 0;
                detail += $", activeContextsAfterDestroy={registeredAfterDestroy}";
            }
            finally
            {
                UnityEngine.Object.Destroy(parentGo);
                UnityEngine.Object.Destroy(childAGroup);
                UnityEngine.Object.Destroy(childBGroup);
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }

            Console.WriteLine($"[Nexus Architecture Stress] Root hierarchy: {detail}");
            Report("23. Root_Hierarchy_ParentChild_PriorityOrder", ok, detail);
        }

        // ── 24. View → ViewRegistration → Context → ViewBinder → Mediator ───────

        private static void Test_View_Mediator_EndToEnd_BindUnbind_PoolReuse()
        {
            var prevCtx = SynchronizationContext.Current;
            var syncCtx = new TestSyncContext();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            bool ok = false;
            string detail = "";
            GameObject rootGo = null;
            GameObject viewGo = null;
            try
            {
                rootGo = new GameObject("SingleRoot");
                var root = rootGo.AddComponent<Root>();
                var recorder = rootGo.AddComponent<RootLifecycleRecorder>();
                InvokePrivate(root, "Awake");

                viewGo = new GameObject("TestViewGo");
                var view = viewGo.AddComponent<TestView>();
                InvokePrivate(view, "OnEnable"); // registers with the single Root's context

                var ctx = root.Context;
                var binder = GetViewBinder(ctx);
                var mediator = GetTestMediator(ctx, view);
                bool boundAfterOnEnable = view.BoundCount == 1;
                bool activeAfterOnEnable = binder.ActiveMediatorCount == 1;
                bool viewBoundField = (bool)typeof(View).GetField("_isBound", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);

                ctx.SignalBus.Fire(new TestViewSignal { Val = 1 });
                int receivedAfterFire1 = mediator?.Received ?? -1;
                bool delivered = mediator != null && mediator.Received == 1;

                ctx.UnregisterView(view);
                var mediatorAfterUnreg = GetTestMediator(ctx, view);
                ctx.SignalBus.Fire(new TestViewSignal { Val = 2 });
                // Pool-return resets Received to 0 (ClearInjectedReferences → IResettable.Reset);
                // the post-unregister fire must deliver nothing → stays 0.
                bool removed = mediator != null && mediator.Received == 0;

                ctx.RegisterView(view);
                ctx.SignalBus.Fire(new TestViewSignal { Val = 3 });
                var mediator2 = GetTestMediator(ctx, view);
                int receivedAfterFire3 = mediator2?.Received ?? -1;
                bool redelivered = mediator2 != null && mediator2.Received == 1;

                bool poolReused = binder.PoolPopCount >= 1;
                bool resets = binder.PoolResetCount >= 1;
                bool noLeak = binder.PoolLeakWarnings == 0;
                bool activeCount = binder.ActiveMediatorCount == 1;
                bool bound = view.BoundCount == 1;

                ok = delivered && removed && redelivered && poolReused && resets && noLeak && activeCount && bound;
                detail = $"boundAfterOnEnable={boundAfterOnEnable} activeAfterOnEnable={activeAfterOnEnable} " +
                    $"_isBoundAfterOnEnable={viewBoundField} viewErrors=[{string.Join(" | ", recorder.Logger.Errors)}] " +
                    $"recv#1={receivedAfterFire1} recv#3={receivedAfterFire3} mediatorAfterUnreg={mediatorAfterUnreg != null} " +
                    $"delivered={delivered} removed={removed} redelivered={redelivered} poolPop={binder.PoolPopCount} " +
                    $"poolReset={binder.PoolResetCount} leaks={binder.PoolLeakWarnings} active={binder.ActiveMediatorCount} viewBound={view.BoundCount}";

                InvokePrivate(view, "OnDisable");
                InvokePrivate(root, "OnDestroy");
            }
            finally
            {
                UnityEngine.Object.Destroy(rootGo);
                UnityEngine.Object.Destroy(viewGo);
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }

            Console.WriteLine($"[Nexus Architecture Stress] View/Mediator: {detail}");
            Report("24. View_Mediator_EndToEnd_BindUnbind_PoolReuse", ok, detail);
        }

        private static TestMediator GetTestMediator(Context ctx, TestView view)
        {
            var binder = GetViewBinder(ctx);
            var field = typeof(ViewBinder).GetField("_activeMediators", BindingFlags.NonPublic | BindingFlags.Instance);
            var map = (Dictionary<IView, IMediator>)field.GetValue(binder);
            return map.TryGetValue(view, out var m) ? (TestMediator)m : null;
        }

        // ── 25. NexusRuntime registry, lookup, metrics, trace buffer ────────────

        private static void Test_NexusRuntime_Registry_ContextLookup_Metrics()
        {
            var contextData = new ContextData { ScopeTag = "ScopeA" };
            var ctxA = ContextFactory.Create(null, contextData);
            var ctxB = ContextFactory.Create();
            bool ok = false;
            try
            {
                bool registered = NexusRuntime.ActiveContexts.Count == 2;
                bool current = NexusRuntime.CurrentContext == ctxA;
                bool scopeLookup = NexusRuntime.GetContext("scopea") == ctxA;
                bool scopeList = NexusRuntime.GetContexts("ScopeA").Count == 1;

                long beforeSignals = NexusRuntime.Metrics.TotalSignalsDispatched;
                ctxA.SignalBus.Fire(new PerfSignal(7));
                long afterSignals = NexusRuntime.Metrics.TotalSignalsDispatched;
                bool counted = afterSignals == beforeSignals + 1;

                NexusRuntime.Metrics.RecordTrace("trace-1");
                NexusRuntime.Metrics.RecordTrace("trace-2");
                var traces = NexusRuntime.Metrics.GetRecentTraces(out int traceCount);
                bool traceOk = traceCount >= 2 && traces.Length >= 2;

                for (int i = 0; i < 260; i++) NexusRuntime.Metrics.RecordTrace("flood-" + i);
                traces = NexusRuntime.Metrics.GetRecentTraces(out traceCount);
                bool ringCapped = traceCount <= 200 && traces.Length <= 200;

                ok = registered && current && scopeLookup && scopeList && counted && traceOk && ringCapped;
                Console.WriteLine($"[Nexus Architecture Stress]   Registry flags: registered={registered} current={current} " +
                    $"scopeLookup={scopeLookup} scopeList={scopeList} counted={counted} traceOk={traceOk} ringCapped={ringCapped} " +
                    $"activeBefore={NexusRuntime.ActiveContexts.Count}");
            }
            finally
            {
                ctxA.Dispose();
                ctxB.Dispose();
                // ContextData is caller-owned (a ScriptableObject config in Unity, like an
                // asset): the context never destroys it. Destroy here for soak-mode hygiene.
                UnityEngine.Object.Destroy(contextData);
                bool unregistered = NexusRuntime.ActiveContexts.Count == 0;
                ok = ok && unregistered;
                Console.WriteLine($"[Nexus Architecture Stress]   After dispose: active={NexusRuntime.ActiveContexts.Count} unregistered={unregistered}");
            }

            Console.WriteLine($"[Nexus Architecture Stress] NexusRuntime registry/metrics: registered=2 scopeLookup=ok signalCounted=ok traceBufferCapped={ok}");
            Report("25. NexusRuntime_Registry_ContextLookup_Metrics", ok,
                "registry=2 contexts, CurrentContext=first, scope lookup case-insensitive, signal metrics incremented, trace ring capped at 200, unregister-on-dispose=0");
        }

        // ── 26. NetworkMonitor event lifecycle ──────────────────────────────────

        private static void Test_NetworkMonitor_Events_Latency_Pruning()
        {
            NetworkMonitor.ClearHistory();
            NetworkMonitor.Enabled = true;
            NetworkMonitor.MaxEvents = 100;
            // Connection status is live state, not history: ClearHistory does not reset it,
            // so seed the baseline instead of assuming a fresh process (soak-mode safe).
            NetworkMonitor.UpdateConnectionStatus(false);

            bool ok = false;
            try
            {
                for (int i = 0; i < 150; i++)
                {
                    NetworkMonitor.RecordSignalSent($"Signal{i % 5}", bytes: i, destination: "server");
                }
                for (int i = 0; i < 30; i++) NetworkMonitor.RecordSignalReceived("Ping", latencyMs: i, bytes: 16, source: "peer");
                NetworkMonitor.RecordSignalFailed("Ping", "timeout");
                NetworkMonitor.RecordSignalTimeout("Ping", 5000f);

                bool pruned = NetworkMonitor.GetRecentEvents(1000).Length <= 100;
                bool failures = NetworkMonitor.GetFailedEventCount() == 2;
                bool failedList = NetworkMonitor.GetFailedEvents().Length == 2;
                bool avg = Math.Abs(NetworkMonitor.GetAverageLatency("Ping") - 14.5f) < 0.01f;
                bool max = NetworkMonitor.GetMaxLatency("Ping") == 29f;
                // Signal counts track RecordSignalSent only (per real NetworkMonitor); received/
                // failed/timeout events exist in the event list but do not bump the counters.
                bool counts = NetworkMonitor.GetSignalCounts().TryGetValue("Signal0", out int sentCount) && sentCount == 30;
                bool bytes = NetworkMonitor.GetTotalBytesSent() > 0 && NetworkMonitor.GetTotalBytesReceived() == 30 * 16;
                bool status = NetworkMonitor.CurrentStatus.IsConnected == false;

                NetworkMonitor.UpdateConnectionStatus(true, "wifi", packetLoss: 0.1f, bandwidthKbps: 1000f);
                bool statusChanged = NetworkMonitor.CurrentStatus.IsConnected && Math.Abs(NetworkMonitor.CurrentStatus.PacketLoss - 0.1f) < 0.001f;

                NetworkMonitor.Enabled = false;
                NetworkMonitor.RecordSignalSent("Disabled", 1);
                bool disabled = Array.TrueForAll(NetworkMonitor.GetRecentEvents(1000), e => e.SignalName != "Disabled");
                NetworkMonitor.Enabled = true;

                ok = pruned && failures && failedList && avg && max && counts && bytes && status && statusChanged && disabled;
            }
            finally
            {
                NetworkMonitor.ClearHistory();
            }

            Console.WriteLine($"[Nexus Architecture Stress] NetworkMonitor: pruned={ok}");
            Report("26. NetworkMonitor_Events_Latency_Pruning", ok,
                "150 sent (pruned to 100), 30 received latency avg=14.5 max=29, failed+timeout=2, disabled flag stops recording, connection status update");
        }

        // ── 27. Plugin trace sink auth + tracing contract ───────────────────────

        private static void Test_PluginTraceSink_Auth_And_TracingContract()
        {
            bool ok = false;
            var ctx = ContextFactory.Create();
            try
            {
                var plugin = new TraceProviderPlugin();
                ctx.RegisterPlugin(plugin);
                bool sinkRegistered = plugin.RegisteredCalls == 1;

                // Without NEXUS_DEBUG the real tracing is compiled out: BeginEvent returns 0,
                // no events reach the sink. Contract must be stable.
                int eventId = NexusTrace.BeginEvent(Nexus.Core.TraceEventType.Signal, "ProbeSignal");
                bool compiledOutContract = eventId == 0 && plugin.Sink.Written == 0;
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
                bool endNoop = NexusTrace.GetRecentEvents(out int count).Length == 0 && count == 0;
                NexusTrace.Reset();

                // Capability enforcement: this plugin can only register trace sinks.
                bool unauthorized = false;
                try
                {
                    var pc = new PluginContext(plugin, ctx);
                    pc.RegisterSignalInterceptor(null);
                }
                catch (UnauthorizedPluginAccessException) { unauthorized = true; }

                ctx.RemovePlugin(plugin);
                bool removed = plugin.RegisteredCalls == 1;

                ok = sinkRegistered && compiledOutContract && endNoop && unauthorized && removed;
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Plugin/tracing: sinkRegistered={ok}");
            Report("27. PluginTraceSink_Auth_And_TracingContract", ok,
                "TraceProvider plugin registers sink, BeginEvent==0 without NEXUS_DEBUG (compiled-out contract), EndEvent/Reset safe, UnauthorizedPluginAccessException on missing capability, RemovePlugin clean");
        }

        // ── 28. Encrypted storage round-trip + tamper detection ─────────────────

        private static void Test_EncryptedStorage_RoundTrip_TamperDetection()
        {
            UnityEngine.PlayerPrefs.ClearAll();
            bool ok = false;
            try
            {
                var s1 = new EncryptedStorageService("HarnessSalt_28") { AutoSave = true };
                s1.SetString("profile", "hero=42");
                bool roundTrip = s1.GetString("profile") == "hero=42";
                s1.SetLong("coins", 12345);
                bool longRt = s1.GetLong("coins") == 12345;
                s1.Dispose();

                // Same device seed + same salt must decrypt the persisted file.
                var s2 = new EncryptedStorageService("HarnessSalt_28");
                bool persisted = s2.GetString("profile") == "hero=42" && s2.GetLong("coins") == 12345;

                // Tamper: flip a ciphertext byte. Read must fail HMAC and return default.
                string export = s2.ExportEncryptedSaveData("profile");
                byte[] tampered = Convert.FromBase64String(export);
                tampered[tampered.Length - 1] ^= 0xFF;
                bool imported = s2.ImportEncryptedSaveData("profile", Convert.ToBase64String(tampered));
                bool tamperDetected = s2.GetString("profile") == "";

                s2.DeleteKey("coins");
                bool deleted = !s2.HasKey("coins");
                s2.Dispose();

                ok = roundTrip && longRt && persisted && imported && tamperDetected && deleted;
            }
            finally
            {
                try { Directory.Delete(Path.Combine(Application.persistentDataPath, "SecureData"), true); }
                catch { /* already gone */ }
                UnityEngine.PlayerPrefs.ClearAll();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Encrypted storage: roundTrip={ok}");
            Report("28. EncryptedStorage_RoundTrip_TamperDetection", ok,
                "AES-256+HMAC round-trip, persistence across instances, ciphertext tamper detected (HMAC revert to default), delete key");
        }

        // ── 29. SaveThrottler + OfflineTimeCalculator + GameSaveManager ─────────

        private static void Test_Storage_SaveThrottler_OfflineTime_GameSave()
        {
            bool ok = false;
            try
            {
                var timeProvider = new FakeTimeProvider { Now = 0f };
                var throttler = new SaveThrottler(null, null, TimeSpan.FromSeconds(2));
                throttler.TimeProvider = timeProvider;

                int saves = 0;
                void saveAction() => saves++;
                throttler.TryRequestSave(saveAction);
                bool immediate = saves == 1;

                throttler.TryRequestSave(saveAction);
                bool pending = saves == 1; // within throttle window

                timeProvider.Now = 3f;
                throttler.Tick(0.016f);
                bool flushedOnTick = saves == 2;

                throttler.ForceSave(saveAction);
                bool forced = saves == 3;

                var prefs = new FakePlayerPrefsService();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                prefs.SetLong("quit", now - 5000);
                long normal = OfflineTimeCalculator.CalculateOfflineSeconds(prefs, key: "quit");
                prefs.SetLong("quit", now - 100000);
                long capped = OfflineTimeCalculator.CalculateOfflineSeconds(prefs, key: "quit");
                prefs.SetLong("quit", now + 3600);
                long manipulated = OfflineTimeCalculator.CalculateOfflineSeconds(prefs, key: "quit");

                var model = new TestSaveModel();
                var gsm = new GameSaveManager();
                gsm.RegisterModel(model);
                gsm.SaveAsync("slot1").GetAwaiter().GetResult();
                bool exists = gsm.SaveExists("slot1");
                model.Data = "v2";
                bool loaded = gsm.LoadAsync("slot1").GetAwaiter().GetResult() && model.Data == "v1";
                gsm.DeleteSave("slot1");
                bool deleted = !gsm.SaveExists("slot1");

                ok = immediate && pending && flushedOnTick && forced
                    && normal == 5000 && capped == 28800 && manipulated == 0
                    && exists && loaded && deleted;
            }
            finally
            {
                try { Directory.Delete(Path.Combine(Application.persistentDataPath, "saves"), true); }
                catch { /* already gone */ }
            }

            Console.WriteLine($"[Nexus Architecture Stress] Storage: throttle={ok}");
            Report("29. Storage_SaveThrottler_OfflineTime_GameSave", ok,
                "SaveThrottler immediate/pending/flush-on-tick/force, OfflineTimeCalculator normal/cap/manipulation, GameSaveManager JSON save/load/delete");
        }

        // ── 30. ObjectPoolService spawn/despawn/reuse ───────────────────────────

        private static void Test_ObjectPoolService_SpawnDespawn_Reuse()
        {
            var svc = new ObjectPoolService();
            GameObject prefab = null;
            bool ok = false;
            try
            {
                svc.InitializeAsync(default).GetAwaiter().GetResult();
                prefab = new GameObject("Bullet");
                var prefabPoolable = prefab.AddComponent<TestPoolable>();

                svc.Prewarm(prefab, 2);
                var a = svc.Spawn(prefab);
                var aPoolable = a.GetComponent<TestPoolable>();
                bool spawned = a != null && a.activeInHierarchy && aPoolable.SpawnCount == 1;

                svc.Despawn(a);
                bool despawned = aPoolable.DespawnCount == 1 && !a.activeInHierarchy;

                var b = svc.Spawn(prefab);
                var bPoolable = b.GetComponent<TestPoolable>();
                bool reused = ReferenceEquals(a, b) && bPoolable.SpawnCount == 2; // re-spawned from pool

                var c = svc.Spawn(prefab);
                bool fresh = !ReferenceEquals(a, c);

                svc.ClearPool(prefab);
                bool poolEmpty = svc.Spawn(prefab) != null; // repopulated from scratch
                var d = svc.Spawn(prefab);
                bool dFresh = !ReferenceEquals(a, d) && !ReferenceEquals(c, d);

                ok = spawned && despawned && reused && fresh && poolEmpty && dFresh;
            }
            finally
            {
                svc.Dispose();
                // The prefab is caller-owned (like an asset in Unity): the pool service
                // never destroys it. Destroy here so soak mode sees no object creep.
                if (prefab != null) UnityEngine.Object.Destroy(prefab);
            }

            Console.WriteLine($"[Nexus Architecture Stress] ObjectPool: reuse={ok}");
            Report("30. ObjectPoolService_SpawnDespawn_Reuse", ok,
                "prewarm, spawn activates + OnSpawned, despawn deactivates + OnDespawned, same instance reused, pool exhaustion creates fresh, ClearPool empties");
        }

        // ── 31. Economy + Progression persistence & integrity ───────────────────

        private static void Test_Economy_And_Progression_Persistence_Integrity()
        {
            var prefs = new FakePlayerPrefsService();
            bool ok = false;
            try
            {
                var eco = new EconomyService { PlayerPrefsService = prefs };
                eco.Earn("gold", 100);
                bool earned = eco.GetBalance("gold") == 100;
                bool afford = eco.CanAfford("gold", 30);
                bool spent = eco.Spend("gold", 30) && eco.GetBalance("gold") == 70;
                bool rejected = !eco.Spend("gold", 1000) && eco.GetBalance("gold") == 70;

                eco.SetBalance("gold", long.MaxValue - 5);
                eco.Earn("gold", 100);
                bool overflowClamped = eco.GetBalance("gold") == long.MaxValue;

                var eco2 = new EconomyService { PlayerPrefsService = prefs };
                bool ecoPersisted = eco2.GetBalance("gold") == long.MaxValue;
                eco2.SetBalance("gold", 0);

                var prog = new ProgressionService { PlayerPrefsService = prefs };
                prog.InitializeAsync(default).GetAwaiter().GetResult();
                prog.CompleteCurrentLevel();
                prog.CompleteCurrentLevel();
                prog.CompleteCurrentLevel();
                bool leveled = prog.CurrentLevel.Value == 4 && prog.MaxUnlockedLevel.Value == 4;
                prog.SetLevel(2);
                bool setLevel = prog.CurrentLevel.Value == 2 && prog.MaxUnlockedLevel.Value == 4;
                bool costGrows = prog.CalculateUpgradeCost(100, 5) > prog.CalculateUpgradeCost(100, 4);

                var prog2 = new ProgressionService { PlayerPrefsService = prefs };
                prog2.InitializeAsync(default).GetAwaiter().GetResult();
                bool progPersisted = prog2.CurrentLevel.Value == 2;

                ok = earned && afford && spent && rejected && overflowClamped && ecoPersisted
                    && leveled && setLevel && costGrows && progPersisted;
            }
            finally
            {
            }

            Console.WriteLine($"[Nexus Architecture Stress] Economy/Progression: integrity={ok}");
            Report("31. Economy_And_Progression_Persistence_Integrity", ok,
                "earn/spend/canAfford, insufficient-funds rejection, long overflow clamp, balance persistence, level-up + max-unlock, exponential cost growth, level persistence");
        }

        // ── 32. ContextBuilder validation + strict injection ────────────────────

        private sealed class MissingDep { }

        private sealed class ValidatedHost
        {
#pragma warning disable 0649 // assigned via DI injection at resolve time
            [Inject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class LazyValidatedHost
        {
#pragma warning disable 0649 // assigned via DI injection at resolve time
            [Inject] public LazyInjection<MissingDep> Dep;
#pragma warning restore 0649
        }

        private sealed class OptionalValidatedHost
        {
#pragma warning disable 0649 // assigned via DI injection at resolve time
            [OptionalInject] public MissingDep Dep;
#pragma warning restore 0649
        }

        private sealed class CtorValidatedHost
        {
            public CtorValidatedHost(MissingDep dep) { }
        }

        private sealed class ValidationLifecycle : IContextLifecycle
        {
            public ContextBuilder Builder;

            public void OnConfigure(IContextBuilder builder)
            {
                Builder = (ContextBuilder)builder;
                builder.EnableStrictInjection();
                builder.Bind<ValidatedHost>();
                builder.Bind<LazyValidatedHost>();
                builder.Bind<OptionalValidatedHost>();
                builder.Bind<CtorValidatedHost>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private static void Test_ContextBuilder_Validate_StrictInjection()
        {
            var ctx = ContextFactory.Create();
            bool ok = false;
            try
            {
                var lifecycle = new ValidationLifecycle();
                ctx.Configure(new[] { lifecycle });

                var issues = lifecycle.Builder.Validate();
                bool missingField = issues.Exists(i => i.SourceType == typeof(ValidatedHost) && i.IssueType == DiValidationIssueType.MissingFieldDependency);
                bool ctorFlagged = issues.Exists(i => i.SourceType == typeof(CtorValidatedHost) && i.IssueType == DiValidationIssueType.MissingConstructorDependency);
                bool lazyNotFlagged = !issues.Exists(i => i.SourceType == typeof(LazyValidatedHost));
                bool optionalNotFlagged = !issues.Exists(i => i.SourceType == typeof(OptionalValidatedHost));

                bool strictThrows = false;
                try { ctx.Resolve<ValidatedHost>(); }
                catch (InvalidOperationException) { strictThrows = true; }

                ok = missingField && ctorFlagged && lazyNotFlagged && optionalNotFlagged && strictThrows;
            }
            finally
            {
                ctx.Dispose();
            }

            Console.WriteLine($"[Nexus Architecture Stress] Builder validation: strict={ok}");
            Report("32. ContextBuilder_Validate_StrictInjection", ok,
                "missing [Inject] field flagged, missing ctor param flagged, LazyInjection not flagged, [OptionalInject] not flagged, strict injection throws on unsatisfiable resolve");
        }
    }
}
