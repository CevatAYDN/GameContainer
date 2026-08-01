// Cross-context + cross-thread proof suite: exercises ONLY the real Nexus runtime
// surfaces and only in the ways the architecture actually guarantees.
//
// C1. CrossContext_ScopeRouting_And_Broadcast:
//     Real contexts (ContextFactory.Create + real ContextData ScopeTags) with the real
//     resolver-driven [CrossContext] broadcast: scope-tagged routing (OrdinalIgnoreCase
//     matching), no-scope broadcast to every other context, self-skip, the
//     HybridQueue -> FireQueued -> cross-context chain, and disposal silencing.
//
// C2. CrossThread_HybridQueue_Ordering_And_ConcurrentLifecycle:
//     The real cross-thread design: HybridQueue.EnqueueThreadSafe from producer threads,
//     DrainThreadSafe on the owning thread. Per-thread enqueue order must be preserved,
//     nothing lost, nothing duplicated. Then concurrent per-context traffic (each bus
//     touched only by its owner thread, cross-thread traffic only through the queue —
//     exactly the model Nexus documents) with concurrent context create/dispose, and a
//     real async-aware drain (SubscribeAsync + queued fire -> FireQueued async path).
//
// Run: dotnet run -c Release            (included in the full pipeline)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nexus.Core;

namespace NexusBench
{
    public static class CrossThreadSuite
    {
        private static int _failures;

        [CrossContext(ScopeTag = "Gameplay")]
        public struct GameplayScopeSignal { public int Val; }

        [CrossContext]
        public struct BroadcastAllSignal { public int Val; }

        public struct CrossThreadSig { public int Producer; public int Seq; }
        public struct LocalTrafficSig { public int Val; }
        public struct AsyncDrainSig { public int Val; }

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus CrossThread] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("CrossThread", name, ok, detail);
            if (!ok) _failures++;
        }

        // ── C1: real cross-context scope routing + broadcast + queued chain ──────────

        private static void CrossContext_ScopeRouting_And_Broadcast()
        {
            var contexts = new List<Context>();
            var contextData = new List<ContextData>();
            bool ok = false;
            string detail = "no detail";
            try
            {
                int a = 0, b = 0, c = 0, d = 0;          // GameplayScopeSignal receivers
                int allA = 0, allB = 0, allC = 0, allD = 0; // BroadcastAllSignal receivers

                var dataA = new ContextData { ScopeTag = "Gameplay" };
                var dataB = new ContextData { ScopeTag = "Gameplay" };
                var dataC = new ContextData { ScopeTag = "UI" };
                var dataD = new ContextData { ScopeTag = "Net" };
                contextData.AddRange(new[] { dataA, dataB, dataC, dataD });

                var ctxA = ContextFactory.Create(null, dataA);
                var ctxB = ContextFactory.Create(null, dataB);
                var ctxC = ContextFactory.Create(null, dataC);
                var ctxD = ContextFactory.Create(null, dataD);
                contexts.AddRange(new[] { ctxA, ctxB, ctxC, ctxD });

                ctxA.SignalBus.Subscribe<GameplayScopeSignal>(_ => a++);
                ctxB.SignalBus.Subscribe<GameplayScopeSignal>(_ => b++);
                ctxC.SignalBus.Subscribe<GameplayScopeSignal>(_ => c++);
                ctxD.SignalBus.Subscribe<GameplayScopeSignal>(_ => d++);
                ctxA.SignalBus.Subscribe<BroadcastAllSignal>(_ => allA++);
                ctxB.SignalBus.Subscribe<BroadcastAllSignal>(_ => allB++);
                ctxC.SignalBus.Subscribe<BroadcastAllSignal>(_ => allC++);
                ctxD.SignalBus.Subscribe<BroadcastAllSignal>(_ => allD++);

                // Scope-tagged routing: firing context ALSO receives locally (normal SignalBus
                // semantics); the broadcast delivers only to the other Gameplay context (B).
                ctxA.SignalBus.Fire(new GameplayScopeSignal { Val = 1 });
                bool step1 = a == 1 && b == 1 && c == 0 && d == 0;

                // Fired from a NON-matching context (D, "Net"): D gets its own local delivery,
                // the broadcast reaches the Gameplay contexts A and B.
                ctxD.SignalBus.Fire(new GameplayScopeSignal { Val = 1 });
                bool step2 = a == 2 && b == 2 && c == 0 && d == 1;

                // No-scope broadcast: every OTHER context receives; the firing context only
                // its local delivery.
                ctxA.SignalBus.Fire(new BroadcastAllSignal { Val = 1 });
                bool step3 = allA == 1 && allB == 1 && allC == 1 && allD == 1;

                // Queued chain: HybridQueue drain fires cross-context too (real FireQueued path).
                ctxA.HybridQueue.EnqueueThreadSafe(new GameplayScopeSignal { Val = 2 });
                ctxA.HybridQueue.DrainThreadSafe();
                bool step4 = a == 3 && b == 3 && c == 0 && d == 1;

                // Disposal silences the target: B's bus auto-disposes, no more broadcasts.
                ctxB.Dispose();
                contexts.Remove(ctxB);
                ctxA.SignalBus.Fire(new GameplayScopeSignal { Val = 3 });
                bool step5 = a == 4 && b == 3 && c == 0 && d == 1;

                // Registry state is exact: A, C, D live; B gone.
                bool step6 = NexusRuntime.ActiveContexts.Count == 3;

                ok = step1 && step2 && step3 && step4 && step5 && step6;
                detail = $"scope=(a={a} b={b} c={c} d={d}) broadcast=(A={allA} B={allB} C={allC} D={allD}) " +
                    $"steps={step1}/{step2}/{step3}/{step4}/{step5}/{step6} active={NexusRuntime.ActiveContexts.Count}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                foreach (var ctx in contexts) ctx.Dispose();
                foreach (var data in contextData) UnityEngine.Object.Destroy(data);
            }

            Report("C1. CrossContext_ScopeRouting_And_Broadcast", ok && NexusRuntime.ActiveContexts.Count == 0, detail);
        }

        // ── C2: cross-thread HybridQueue ordering + concurrent lifecycle + async drain ──

        private sealed class WorkerState
        {
            public volatile Context Ctx;
            public volatile bool Ready;
            public readonly List<(int Producer, int Seq)> Received = new();
            public readonly HashSet<int> Seen = new();
            public volatile bool OrderBroken;
            public volatile Exception Error;
        }

        private static void CrossThread_HybridQueue_Ordering_And_ConcurrentLifecycle()
        {
            bool ok = false;
            string detail = "no detail";
            try
            {
                // ── Part 1: producer threads -> real HybridQueue -> owning-thread drain ──
                var ctx = ContextFactory.Create();
                const int producers = 8;
                const int perProducer = 2000;
                const int total = producers * perProducer;
                var received = new List<(int Producer, int Seq)>();
                var seen = new HashSet<int>();
                ctx.SignalBus.Subscribe<CrossThreadSig>(s =>
                {
                    lock (received) received.Add((s.Producer, s.Seq));
                });

                var threads = new Thread[producers];
                for (int t = 0; t < producers; t++)
                {
                    int producer = t;
                    threads[t] = new Thread(() =>
                    {
                        for (int seq = 0; seq < perProducer; seq++)
                        {
                            ctx.HybridQueue.EnqueueThreadSafe(new CrossThreadSig { Producer = producer, Seq = seq });
                        }
                    });
                    threads[t].Start();
                }

                // Drain on the owning thread until everything arrived (watchdog 30s).
                var sw = Stopwatch.StartNew();
                while (ctx.HybridQueue.TotalDrained < total && sw.ElapsedMilliseconds < 30000)
                {
                    ctx.HybridQueue.DrainThreadSafe();
                    Thread.Sleep(1);
                }
                for (int t = 0; t < producers; t++) threads[t].Join();

                // All producers are done now: enqueue total is fixed. Final drain until the
                // queue is fully empty AND the drained total matches — proves nothing was
                // lost to the producer/drain interleaving.
                sw.Restart();
                while ((ctx.HybridQueue.TotalDrained < total || ctx.HybridQueue.ThreadSafeQueueDepth > 0)
                       && sw.ElapsedMilliseconds < 30000)
                {
                    ctx.HybridQueue.DrainThreadSafe();
                    Thread.Sleep(1);
                }
                ctx.HybridQueue.DrainThreadSafe();

                long drained = ctx.HybridQueue.TotalDrained;
                int delivered = 0;
                lock (received) delivered = received.Count;
                lock (received)
                {
                    foreach (var (p, s) in received) seen.Add(p * 100000 + s);
                }
                // Per-thread relative order must be preserved (ring buffer FIFO per queue).
                bool orderOk = true;
                var lastSeq = new int[producers];
                lock (received)
                {
                    foreach (var (p, s) in received)
                    {
                        if (s < lastSeq[p]) { orderOk = false; break; }
                        lastSeq[p] = s;
                    }
                }
                bool part1 = drained == total && delivered == total && seen.Count == total
                    && orderOk && ctx.HybridQueue.ThreadSafeQueueDepth == 0;
                ctx.Dispose();

                // ── Part 2: concurrent contexts + per-owner traffic + queue-based cross-thread ──
                const int workers = 8;
                const int producerThreads = 4;
                const int perQueue = 500; // per producer thread, per worker context
                var workersState = new WorkerState[workers];
                int readyCount = 0;
                int doneCount = 0;

                for (int w = 0; w < workers; w++)
                {
                    int index = w;
                    var data = new ContextData { ScopeTag = $"Worker{index}" };
                    var state = new WorkerState();
                    workersState[w] = state;
                    var t = new Thread(() =>
                    {
                        try
                        {
                            var workerCtx = ContextFactory.Create(null, data);
                            state.Ctx = workerCtx;

                            // Per-owner traffic: the owner is the only thread touching its bus.
                            // Subscriptions MUST exist before Ready is published — producers
                            // only enqueue to Ready workers, and any enqueue that lands before
                            // the subscription would be drained into a bus with no receiver.
                            int local = 0;
                            workerCtx.SignalBus.Subscribe<LocalTrafficSig>(_ => local++);
                            workerCtx.SignalBus.Subscribe<CrossThreadSig>(s => state.Received.Add((s.Producer, s.Seq)));
                            state.Ready = true;
                            Interlocked.Increment(ref readyCount);

                            for (int i = 0; i < 100; i++) workerCtx.SignalBus.Fire(new LocalTrafficSig { Val = i });
                            if (local != 100) throw new InvalidOperationException($"local traffic lost: {local}/100");

                            // Cross-thread traffic arrives only via the context's own queue
                            // (the architecture's documented cross-thread channel).
                            int expected = producerThreads * perQueue;
                            var sw2 = Stopwatch.StartNew();
                            while (state.Received.Count < expected && sw2.ElapsedMilliseconds < 30000)
                            {
                                workerCtx.HybridQueue.DrainThreadSafe();
                                Thread.Sleep(1);
                            }
                            workerCtx.HybridQueue.DrainThreadSafe();
                            if (state.Received.Count != expected)
                                throw new InvalidOperationException($"queue traffic lost: {state.Received.Count}/{expected}");

                            workerCtx.Dispose();
                        }
                        catch (Exception ex)
                        {
                            state.Error = ex;
                        }
                        finally
                        {
                            UnityEngine.Object.Destroy(data);
                            state.Ctx = null;
                            state.Ready = false;
                            Interlocked.Increment(ref doneCount);
                        }
                    });
                    t.IsBackground = true;
                    t.Start();
                }

                // Producers enqueue into every worker context's queue concurrently.
                // Producers wait until ALL workers are ready: with cold JIT the workers may
                // take tens of ms to reach Ready, and a producer that finishes its loop
                // before then would correctly enqueue nothing — starving the worker. The
                // synchronization is test plumbing, not a runtime guarantee we test.
                sw.Restart();
                while (Volatile.Read(ref readyCount) < workers && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                var producersThreads = new Thread[producerThreads];
                for (int p = 0; p < producerThreads; p++)
                {
                    int producer = p;
                    producersThreads[p] = new Thread(() =>
                    {
                        for (int seq = 0; seq < perQueue; seq++)
                        {
                            for (int w = 0; w < workers; w++)
                            {
                                var ws = workersState[w];
                                // enqueue is lock-protected inside HybridQueue — safe from any thread
                                if (ws.Ready) ws.Ctx.HybridQueue.EnqueueThreadSafe(new CrossThreadSig { Producer = producer, Seq = seq });
                            }
                        }
                    });
                    producersThreads[p].Start();
                }
                for (int p = 0; p < producerThreads; p++) producersThreads[p].Join();

                // Owners may finish after all producers; wait for full teardown (watchdog 40s).
                sw.Restart();
                while (Volatile.Read(ref doneCount) < workers && sw.ElapsedMilliseconds < 40000) Thread.Sleep(5);

                bool part2 = true;
                for (int w = 0; w < workers; w++)
                {
                    if (workersState[w].Error != null)
                    {
                        part2 = false;
                        detail = $"worker{w} EXCEPTION: {workersState[w].Error.Message}";
                        break;
                    }
                    if (workersState[w].Received.Count != producerThreads * perQueue) part2 = false;
                }
                if (part2 && NexusRuntime.ActiveContexts.Count != 0) part2 = false;

                // ── Part 3: real async-aware queued drain (FireQueued async path) ──
                var asyncCtx = ContextFactory.Create();
                int asyncDelivered = 0;
                asyncCtx.SignalBus.SubscribeAsync<AsyncDrainSig>((_, __) =>
                {
                    asyncDelivered++;
                    return default;
                });
                for (int i = 0; i < 100; i++) asyncCtx.HybridQueue.EnqueueThreadSafe(new AsyncDrainSig { Val = i });
                asyncCtx.HybridQueue.DrainThreadSafe();
                sw.Restart();
                while (asyncDelivered < 100 && sw.ElapsedMilliseconds < 15000) Thread.Sleep(5);
                bool part3 = asyncDelivered == 100;
                asyncCtx.Dispose();

                ok = part1 && part2 && part3;
                if (part2) detail = $"part1: drained={drained}/{total} delivered={delivered} unique={seen.Count} " +
                    $"order={orderOk} depth={ctx.HybridQueue.ThreadSafeQueueDepth} | " +
                    $"part2: workers={doneCount}/{workers} active={NexusRuntime.ActiveContexts.Count} | " +
                    $"part3: asyncDelivered={asyncDelivered}/100";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Report("C2. CrossThread_HybridQueue_Ordering_And_ConcurrentLifecycle", ok, detail);
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Nexus CrossThread] CROSS-CONTEXT + CROSS-THREAD PROOF");
            Console.WriteLine("===============================================================================");
            CrossContext_ScopeRouting_And_Broadcast();
            CrossThread_HybridQueue_Ordering_And_ConcurrentLifecycle();
            Console.WriteLine(_failures == 0
                ? "[Nexus CrossThread] ALL CROSS-CONTEXT/CROSS-THREAD TESTS PASSED ✓"
                : $"[Nexus CrossThread] {_failures} TEST(S) FAILED ✗");
            return _failures;
        }
    }
}
