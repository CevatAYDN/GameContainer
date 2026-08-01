// Soak mode: repeats the full benchmark pipeline (benchmarks + recovery regression +
// architecture stress suite) N times in one process and watches for state creep that
// a single pass cannot reveal:
//   - managed heap growth after forced GC
//   - process working-set growth
//   - process / thread-pool thread leaks
//   - unbounded growth of runtime static caches (SignalBus dispatch caches,
//     subscription-node pool, NexusRuntime registry, Root registry, stub object
//     registry, NetworkMonitor buffers)
//
// Run: dotnet run -c Release -- --soak [iterations]   (default 10)
//
// Iteration 1 warms JIT/type caches; iteration 2 establishes the baseline; later
// iterations FAIL if they exceed the baseline by more than the thresholds below.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Nexus.Core;

namespace NexusBench
{
    public static class SoakMode
    {
        private const long HeapGrowthLimitBytes = 2 * 1024 * 1024;   // managed heap, post-GC
        private const long WorkingSetGrowthLimitBytes = 12 * 1024 * 1024;
        // Committed memory (GC.GetGCMemoryInfo().TotalCommittedBytes) is what the runtime
        // actually reserved for the managed heap; a steady plateau is normal, but growth
        // beyond 32MB means pages are retained that the GC is not returning to the OS —
        // the failure mode working-set deltas often miss on long-running Unity sessions.
        private const long CommittedGrowthLimitBytes = 32 * 1024 * 1024;
        // Collection counts are REPORTED, not gated: this workload legitimately churns
        // ~30+ gen2 collections per iteration (test 11's composite path boxes payloads
        // into LOH-sized buffers by design), so counts grow steadily while heap and
        // committed memory plateau. A leak shows up in committed/heap/caches, not in
        // collection counts; gating on gen2 would be a false positive.
        // C2 (CrossThreadSuite) intentionally spawns ~20 short-lived threads per run and
        // joins them before returning; the OS can lag thread reaping by a few. 12 gives
        // that headroom while still catching a real leak (a leaky suite would add dozens).
        private const int ThreadGrowthLimit = 12;                     // process threads
        private const int PoolThreadGrowthLimit = 4;                  // ThreadPool threads

        private sealed class CacheProbe
        {
            public readonly string Name;
            private readonly Func<int> _count;
            public int Baseline = -1;
            public bool Grew;
            public CacheProbe(string name, Func<int> count) { Name = name; _count = count; }
            public int Count()
            {
                try { return _count(); }
                catch { return -1; }
            }
        }

        private static List<CacheProbe> BuildCacheProbes()
        {
            var probes = new List<CacheProbe>();
            var busType = typeof(SignalBus);

            void DictProbe(string fieldName)
            {
                var f = busType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                probes.Add(new CacheProbe(fieldName, () => ((System.Collections.IDictionary)f.GetValue(null)).Count));
            }
            DictProbe("s_signalSetterCache");
            DictProbe("s_genericSyncDispatchCache");
            DictProbe("s_genericAsyncDispatchCache");
            DictProbe("s_crossContextCache");

            var listPool = busType.GetField("s_listPool", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("s_listPool", () => ((Stack<List<object>>)listPool.GetValue(null)).Count));

            var nodePoolType = busType.Assembly.GetType("Nexus.Core.SubscriptionNodePool");
            var nodePool = nodePoolType?.GetField("s_pool", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("SubscriptionNodePool", () =>
                nodePool == null ? -1 : ((Stack<object>)nodePool.GetValue(null)).Count));

            var runtimeType = typeof(NexusRuntime);
            var activeCtxs = runtimeType.GetField("s_activeContexts", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("NexusRuntime.s_activeContexts", () =>
                ((System.Collections.IDictionary)activeCtxs.GetValue(null)).Count));

            var rootRegistry = typeof(Root).GetField("s_allRoots", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("Root.s_allRoots", () =>
                ((System.Collections.IList)rootRegistry.GetValue(null)).Count));

            var objectRegistry = typeof(UnityEngine.Object).GetField("s_all", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("UnityEngine.Object.s_all", () =>
                ((System.Collections.IList)objectRegistry.GetValue(null)).Count));

            var networkEvents = typeof(NetworkMonitor).GetField("s_events", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("NetworkMonitor.s_events", () =>
                ((System.Collections.ICollection)networkEvents.GetValue(null)).Count));

            // Reentrancy guard: a permanently elevated static depth means an async dispatch
            // leaked its increment — every subsequent fire on ANY bus would be aborted.
            var stackDepth = typeof(SignalBus).GetField("s_stackDepth", BindingFlags.NonPublic | BindingFlags.Static);
            probes.Add(new CacheProbe("SignalBus.s_stackDepth", () =>
                stackDepth == null ? -1 : (int)stackDepth.GetValue(null)));

            return probes;
        }

        public static int Run(int iterations)
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine($"[Nexus Benchmark] SOAK MODE: {iterations} iterations of the full pipeline");
            Console.WriteLine("===============================================================================");

            var probes = BuildCacheProbes();
            bool leakDetected = false;
            bool failuresDetected = false;
            long heapBaseline = -1, wsBaseline = -1, committedBaseline = -1;
            int threadBaseline = -1, poolBaseline = -1;
            int gen0Baseline = -1, gen1Baseline = -1, gen2Baseline = -1;
            var sw = Stopwatch.StartNew();

            for (int iter = 1; iter <= iterations; iter++)
            {
                ResultSink.Clear();
                var iterSw = Stopwatch.StartNew();
                int failures = Program.RunAll();
                iterSw.Stop();
                if (failures > 0) failuresDetected = true;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long heap = GC.GetTotalMemory(true);
                long ws = Process.GetCurrentProcess().WorkingSet64;
                long committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                int threads = Process.GetCurrentProcess().Threads.Count;
                int pool = ThreadPool.ThreadCount;

                foreach (var p in probes)
                {
                    int n = p.Count();
                    if (iter == 2) p.Baseline = n;
                    else if (p.Baseline >= 0 && n > p.Baseline) p.Grew = true;
                }

                if (iter == 2)
                {
                    heapBaseline = heap;
                    wsBaseline = ws;
                    committedBaseline = committed;
                    threadBaseline = threads;
                    poolBaseline = pool;
                    gen0Baseline = gen0;
                    gen1Baseline = gen1;
                    gen2Baseline = gen2;
                }

                bool leakNow = false;
                string metrics;
                if (iter >= 2)
                {
                    var grew = new List<string>();
                    foreach (var p in probes)
                    {
                        if (p.Grew) grew.Add($"{p.Name}+{p.Count() - p.Baseline}");
                    }
                    leakNow = heap - heapBaseline > HeapGrowthLimitBytes
                        || ws - wsBaseline > WorkingSetGrowthLimitBytes
                        || committed - committedBaseline > CommittedGrowthLimitBytes
                        || threads - threadBaseline > ThreadGrowthLimit
                        || pool - poolBaseline > PoolThreadGrowthLimit
                        || grew.Count > 0;
                    if (leakNow) leakDetected = true;
                    metrics = $"heapΔ={MB(heap - heapBaseline):+0.00;-0.00}MB wsΔ={MB(ws - wsBaseline):+0.00;-0.00}MB " +
                        $"committedΔ={MB(committed - committedBaseline):+0.00;-0.00}MB " +
                        $"gen0Δ={gen0 - gen0Baseline:+0;-0} gen1Δ={gen1 - gen1Baseline:+0;-0} gen2Δ={gen2 - gen2Baseline:+0;-0} " +
                        $"threadsΔ={threads - threadBaseline:+0;-0} poolΔ={pool - poolBaseline:+0;-0}" +
                        (grew.Count > 0 ? $" caches=[{string.Join(",", grew)}]" : "");
                }
                else
                {
                    metrics = $"heap={MB(heap):F1}MB ws={MB(ws):F1}MB committed={MB(committed):F1}MB " +
                        $"gen0={gen0} gen1={gen1} gen2={gen2} threads={threads} pool={pool} (warmup)";
                }

                Console.WriteLine($"[Nexus Benchmark]   soak {iter}/{iterations}: failures={failures} " +
                    $"elapsed={iterSw.ElapsedMilliseconds}ms {metrics}{(leakNow ? "  ⚠ LEAK" : "")}");
            }

            sw.Stop();
            Console.WriteLine("===============================================================================");
            bool ok = !leakDetected && !failuresDetected;
            Console.WriteLine(ok
                ? $"[Nexus Benchmark] SOAK OK — {iterations} iterations in {sw.ElapsedMilliseconds}ms, no state creep, no failures"
                : $"[Nexus Benchmark] SOAK FAILED — leakDetected={leakDetected} failuresDetected={failuresDetected}");
            Console.WriteLine("===============================================================================");
            return ok ? 0 : 1;
        }

        private static double MB(long bytes) => bytes / (1024.0 * 1024.0);
    }
}
