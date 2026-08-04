// Allocation-source diagnostic for the zero-GC benchmark failure.
// Isolates: (a) empty-fire core path, (b) AsyncLocal vs ThreadStatic depth counter,
// (c) per-fire GetCustomAttribute<CrossContextAttribute> reflection,
// (d) full command path, (e) pool round-trip vs Inject split,
// (f) fire-with-command on a PRE-FILLED pool, (g) FieldInfo.SetValue null vs value,
// (h) pool primitives (lock/Interlocked/Stack/HashSet), (i) pool-stats delta during fire,
// (j) ClearInjectedReferences isolated, (k) Inject-after-clear (pooled state),
// (l) fire on truly pre-filled pool, (m) fire on a no-[Inject] command.
//
// Run: dotnet run -c Release -- --alloc-diag

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Nexus.Core;

namespace NexusBench
{
    public readonly struct EmptySignal
    {
        public readonly int Index;
        public EmptySignal(int index) => Index = index;
    }

    public readonly struct CmdSignal
    {
        public readonly int Index;
        public CmdSignal(int index) => Index = index;
    }

    public class CmdCounter { public int Value; }

    public class CmdOnlyCommand : ICommand<CmdSignal>
    {
        [Inject] public CmdCounter Counter;
        public void Execute(CmdSignal signal) { Counter.Value++; }
    }

    /// <summary>Command with NO [Inject] fields — isolates the Inject call from the fire path.</summary>
    public class NoInjectCommand : ICommand<CmdSignal>
    {
        public static int Executions;
        public void Execute(CmdSignal signal) { Executions++; }
    }

    public static class Diagnostics
    {
        private static readonly AsyncLocal<int> s_asyncLocalDepth = new();

        // Boxed-holder variant: the production SignalBus guard stores its async depth in a
        // mutable box (AsyncLocal<AsyncStackDepthBox>) so the decrement is a plain field
        // write and nested dispatches never touch AsyncLocal at all.
        private sealed class Box { public int Value; }
        private static readonly AsyncLocal<Box> s_asyncLocalBox = new();

        [ThreadStatic]
        private static int s_threadStaticDepth;

        public static bool Run()
        {
            int failures = 0;

            // (a) Empty-fire core path: no handlers, no subscribers, no composite.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: empty-fire core path (no handlers/subs) ===");
            failures += MeasureEmptyFire();

            // (b) AsyncLocal vs ThreadStatic increment/decrement — the depth counter pattern.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: depth counter primitive (5000 ++/--) ===");
            failures += MeasureDepthPrimitives();

            // (c) Per-fire GetCustomAttribute<CrossContextAttribute> reflection.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: GetCustomAttribute<CrossContextAttribute> per call (5000) ===");
            failures += MeasureCustomAttribute();

            // (d) Full command-execution path (GetCommand → Inject → Execute → ReturnCommand).
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: fire-with-command path (GetCommand/Inject/ReturnCommand) ===");
            failures += MeasureFireWithCommand();

            // (e) Command-path split: pool round-trip vs per-dispatch Inject reflection.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: pool round-trip vs Inject (5000 each) ===");
            failures += MeasureCommandSplit();

            // (f) Fire-with-command on a TRULY pre-filled pool (initialSize=100 pre-creates at ctor).
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: fire-with-command (pool pre-filled to 100) ===");
            failures += MeasureFireWithPrefilledPool();

            // (g) FieldInfo.SetValue(null) vs SetValue(value) — is the Clearer's null set the allocator?
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: FieldInfo.SetValue null vs value (5000 each) ===");
            failures += MeasureSetValuePrimitive();

            // (h) Pool primitive microbench: which of lock/Interlocked/Stack/HashSet allocates?
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: pool primitives (lock/Interlocked/Stack/HashSet) ===");
            failures += MeasurePoolPrimitives();

            // (i) Pool stats delta across a measured fire loop — is the factory running per dispatch?
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: pool stats delta during fire loop ===");
            failures += MeasurePoolStatsDelta();

            // (j) ClearInjectedReferences in isolation (full method, not just SetValue).
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: NexusDI.ClearInjectedReferences isolated ===");
            failures += MeasureClearInjectedReferences();

            // (k) Inject on a pooled-state instance (field nulled by a prior clear).
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: Inject-after-clear (pooled state) ===");
            failures += MeasureInjectAfterClear();

            // (l) Fire on a command with NO [Inject] fields — isolates Inject entirely.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: fire with no-[Inject] command ===");
            failures += MeasureFireNoInjectCommand();

            // (m) Async dispatch of a SYNC command — the ExecuteAsync<TSignal> sync-command
            // branch previously created an inline lambda capturing 'signal' on every dispatch
            // (closure display-class hoisted to method entry). Must be 0 bytes after the fix.
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === DIAG: FireAsync with sync command (5000) ===");
            failures += MeasureFireAsyncSyncCommand().GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] DIAGNOSTICS DONE"
                : $"[Nexus Benchmark] {failures} DIAGNOSTIC(S) FAILED");
            return failures == 0;
        }

        private static int MeasureEmptyFire()
        {
            var container = new NexusDI();
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());

            try
            {
                // Warm up.
                for (int i = 0; i < 100; i++) bus.Fire(new EmptySignal(i));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new EmptySignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                long perDispatch = allocated / 5000;
                Console.WriteLine($"[Nexus Benchmark] empty fire: allocated={allocated} bytes for 5000 ({perDispatch} bytes/dispatch)");
                bool ok = allocated <= 128;
                Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  empty-fire allocation (limit <=128)");
                return ok ? 0 : 1;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static int MeasureDepthPrimitives()
        {
            // Warm up both.
            for (int i = 0; i < 100; i++)
            {
                s_asyncLocalDepth.Value++;
                s_asyncLocalDepth.Value--;
                s_threadStaticDepth++;
                s_threadStaticDepth--;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long asyncStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                s_asyncLocalDepth.Value++;
                s_asyncLocalDepth.Value--;
            }
            long asyncAlloc = GC.GetAllocatedBytesForCurrentThread() - asyncStart;

            // Boxed holder: entry get-or-create + field ++/-- (mirrors SignalBus's guard).
            long boxStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                var box = s_asyncLocalBox.Value;
                if (box == null) { box = new Box(); s_asyncLocalBox.Value = box; }
                box.Value++;
                box.Value--;
            }
            long boxAlloc = GC.GetAllocatedBytesForCurrentThread() - boxStart;

            long tsStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                s_threadStaticDepth++;
                s_threadStaticDepth--;
            }
            long tsAlloc = GC.GetAllocatedBytesForCurrentThread() - tsStart;

            Console.WriteLine($"[Nexus Benchmark] AsyncLocal<int> ++/--: {asyncAlloc} bytes for 5000 ({asyncAlloc / 5000} bytes/op)");
            Console.WriteLine($"[Nexus Benchmark] AsyncLocal<boxed> ++/--: {boxAlloc} bytes for 5000 ({boxAlloc / 5000} bytes/op)");
            Console.WriteLine($"[Nexus Benchmark] ThreadStatic int ++/--: {tsAlloc} bytes for 5000 ({tsAlloc / 5000} bytes/op)");
            return 0;
        }

        private static int MeasureCustomAttribute()
        {
            for (int i = 0; i < 100; i++)
            {
                var t = typeof(EmptySignal).GetCustomAttribute<CrossContextAttribute>();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long start = GC.GetAllocatedBytesForCurrentThread();
            object attr = null;
            for (int i = 0; i < 5000; i++)
            {
                attr = typeof(EmptySignal).GetCustomAttribute<CrossContextAttribute>();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Console.WriteLine($"[Nexus Benchmark] GetCustomAttribute<CrossContextAttribute>: {allocated} bytes for 5000 ({allocated / 5000} bytes/call) attr={attr ?? "null"}");
            return 0;
        }

        private static int MeasureFireWithCommand()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(CmdOnlyCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                for (int i = 0; i < 100; i++) bus.Fire(new CmdSignal(i));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new CmdSignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                long perDispatch = allocated / 5000;
                Console.WriteLine($"[Nexus Benchmark] fire-with-command: allocated={allocated} bytes for 5000 ({perDispatch} bytes/dispatch) counter={counter.Value}");
                bool ok = allocated <= 128;
                Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  fire-with-command allocation (limit <=128)");
                return ok ? 0 : 1;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static int MeasureCommandSplit()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);

            try
            {
                // Warm the pool with one instance so GetCommand is a true pool hit.
                var warm = poolManager.GetCommand(typeof(CmdOnlyCommand));
                poolManager.ReturnCommand(typeof(CmdOnlyCommand), warm);

                // (e1) Pool round-trip only.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long poolStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = poolManager.GetCommand(typeof(CmdOnlyCommand));
                    poolManager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long poolAlloc = GC.GetAllocatedBytesForCurrentThread() - poolStart;
                Console.WriteLine($"[Nexus Benchmark] pool round-trip: {poolAlloc} bytes for 5000 ({poolAlloc / 5000} bytes/op)");

                // (e2) Per-dispatch Inject reflection on a fresh instance each time.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long injStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = new CmdOnlyCommand();
                    container.Inject(cmd);
                }
                long injAlloc = GC.GetAllocatedBytesForCurrentThread() - injStart;
                Console.WriteLine($"[Nexus Benchmark] Inject(new instance): {injAlloc} bytes for 5000 ({injAlloc / 5000} bytes/op)");

                // (e3) Inject on a REUSED instance (no `new` allocation — isolates reflection cost).
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long injReuseStart = GC.GetAllocatedBytesForCurrentThread();
                var reused = new CmdOnlyCommand();
                for (int i = 0; i < 5000; i++)
                {
                    container.Inject(reused);
                }
                long injReuseAlloc = GC.GetAllocatedBytesForCurrentThread() - injReuseStart;
                Console.WriteLine($"[Nexus Benchmark] Inject(reused instance, no new): {injReuseAlloc} bytes for 5000 ({injReuseAlloc / 5000} bytes/op)");

                // (e4) GetCommand alone (no Return) on the warmed pool.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long getStart = GC.GetAllocatedBytesForCurrentThread();
                object[] cmds = new object[5000];
                for (int i = 0; i < 5000; i++)
                {
                    cmds[i] = poolManager.GetCommand(typeof(CmdOnlyCommand));
                }
                long getAlloc = GC.GetAllocatedBytesForCurrentThread() - getStart;
                Console.WriteLine($"[Nexus Benchmark] GetCommand alone (empty pool, creating): {getAlloc} bytes for 5000 ({getAlloc / 5000} bytes/op)");
                for (int i = 0; i < 5000; i++)
                {
                    poolManager.ReturnCommand(typeof(CmdOnlyCommand), cmds[i]);
                }

                // (e5) ReturnCommand alone (pool now full) — isolates Cleanup/SetValue(null).
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long retStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = poolManager.GetCommand(typeof(CmdOnlyCommand));
                    poolManager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long retAlloc = GC.GetAllocatedBytesForCurrentThread() - retStart;
                Console.WriteLine($"[Nexus Benchmark] Get+Return round-trip (warm pool): {retAlloc} bytes for 5000 ({retAlloc / 5000} bytes/op)");

                return 0;
            }
            finally
            {
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static int MeasureFireWithPrefilledPool()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            // initialSize=100 pre-creates 100 instances in the CommandPool ctor — a TRULY full pool.
            var poolManager = new CommandPoolManager(container, initialSize: 100, maxSize: 100);
            var bus = new SignalBus(container, poolManager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(CmdOnlyCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                // First fire creates the pool (lazy); take the baseline AFTER it exists.
                bus.Fire(new CmdSignal(-1));
                var stats0 = poolManager.GetPoolStatsSnapshot();
                for (int i = 0; i < 100; i++) bus.Fire(new CmdSignal(i));
                var stats1 = poolManager.GetPoolStatsSnapshot();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new CmdSignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                var stats2 = poolManager.GetPoolStatsSnapshot();
                long perDispatch = allocated / 5000;
                int available0 = stats0.Count > 0 ? stats0[0].Available : -1;
                int available1 = stats1.Count > 0 ? stats1[0].Available : -1;
                int available2 = stats2.Count > 0 ? stats2[0].Available : -1;
                long created1 = stats1.Count > 0 ? stats1[0].TotalCreated : -1;
                long created2 = stats2.Count > 0 ? stats2[0].TotalCreated : -1;
                long createdDelta = (created1 >= 0 && created2 >= 0) ? created2 - created1 : -1;
                Console.WriteLine($"[Nexus Benchmark] fire-with-command (pre-filled pool): allocated={allocated} bytes for 5000 ({perDispatch} bytes/dispatch) counter={counter.Value}");
                Console.WriteLine($"[Nexus Benchmark]   available {available0}->{available1}->{available2}, createdDelta during fire={createdDelta}");
                return 0;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static int MeasureSetValuePrimitive()
        {
            var obj = new CmdOnlyCommand();
            var field = typeof(CmdOnlyCommand).GetField("Counter", BindingFlags.Public | BindingFlags.Instance);
            var value = new CmdCounter();

            // SetValue(obj, null) — the Clearer path.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long s1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) field.SetValue(obj, null);
            long a1 = GC.GetAllocatedBytesForCurrentThread() - s1;
            Console.WriteLine($"[Nexus Benchmark] FieldInfo.SetValue(obj, null): {a1} bytes for 5000 ({a1 / 5000} bytes/op)");

            // SetValue(obj, value) — the Inject path.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long s2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) field.SetValue(obj, value);
            long a2 = GC.GetAllocatedBytesForCurrentThread() - s2;
            Console.WriteLine($"[Nexus Benchmark] FieldInfo.SetValue(obj, value): {a2} bytes for 5000 ({a2 / 5000} bytes/op)");

            return 0;
        }

        private static int MeasurePoolPrimitives()
        {
            var lockObj = new object();
            long counter = 0;
            var stack = new Stack<object>();
            var set = new HashSet<object>();
            var items = new object[16];
            for (int i = 0; i < 16; i++) items[i] = new object();
            foreach (var it in items) { stack.Push(it); set.Add(it); }

            // Warm the lock's sync block BEFORE measuring (first Monitor.Enter lazily allocates it).
            lock (lockObj) { }

            // lock
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long lStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) { lock (lockObj) { } }
            long lAlloc = GC.GetAllocatedBytesForCurrentThread() - lStart;
            Console.WriteLine($"[Nexus Benchmark] lock: {lAlloc} bytes for 5000 ({lAlloc / 5000} bytes/op)");

            // Interlocked.Increment on long
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long iStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) Interlocked.Increment(ref counter);
            long iAlloc = GC.GetAllocatedBytesForCurrentThread() - iStart;
            Console.WriteLine($"[Nexus Benchmark] Interlocked.Increment(long): {iAlloc} bytes for 5000 ({iAlloc / 5000} bytes/op)");

            // Stack.Push/Pop (warm, capacity 16)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long sStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                var x = stack.Pop();
                stack.Push(x);
            }
            long sAlloc = GC.GetAllocatedBytesForCurrentThread() - sStart;
            Console.WriteLine($"[Nexus Benchmark] Stack.Push/Pop: {sAlloc} bytes for 5000 ({sAlloc / 5000} bytes/op)");

            // HashSet.Add/Remove (warm)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long hStart = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                var x = items[i & 15];
                set.Remove(x);
                set.Add(x);
            }
            long hAlloc = GC.GetAllocatedBytesForCurrentThread() - hStart;
            Console.WriteLine($"[Nexus Benchmark] HashSet.Add/Remove: {hAlloc} bytes for 5000 ({hAlloc / 5000} bytes/op)");

            return 0;
        }

        private static int MeasurePoolStatsDelta()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(CmdOnlyCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                for (int i = 0; i < 100; i++) bus.Fire(new CmdSignal(i));

                var before = poolManager.GetPoolStatsSnapshot();
                long createdBefore = before.Count > 0 ? before[0].TotalCreated : -1;
                long getsBefore = before.Count > 0 ? before[0].TotalGets : -1;
                long returnsBefore = before.Count > 0 ? before[0].TotalReturns : -1;
                int availableBefore = before.Count > 0 ? before[0].Available : -1;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new CmdSignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                var after = poolManager.GetPoolStatsSnapshot();
                long createdAfter = after.Count > 0 ? after[0].TotalCreated : -1;
                long getsAfter = after.Count > 0 ? after[0].TotalGets : -1;
                long returnsAfter = after.Count > 0 ? after[0].TotalReturns : -1;
                int availableAfter = after.Count > 0 ? after[0].Available : -1;

                Console.WriteLine($"[Nexus Benchmark] fire 5000: allocated={allocated} ({allocated / 5000} bytes/dispatch)");
                Console.WriteLine($"[Nexus Benchmark] pool stats delta: created={createdAfter - createdBefore}, gets={getsAfter - getsBefore}, returns={returnsAfter - returnsBefore}, available {availableBefore}->{availableAfter}");
                return 0;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static int MeasureClearInjectedReferences()
        {
            var instance = new CmdOnlyCommand();
            var container = new NexusDI();
            container.BindInstance(new CmdCounter());

            // Warm metadata caches.
            container.Inject(instance);
            NexusDI.ClearInjectedReferences(instance);
            for (int i = 0; i < 100; i++) NexusDI.ClearInjectedReferences(instance);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) NexusDI.ClearInjectedReferences(instance);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine($"[Nexus Benchmark] ClearInjectedReferences(reused): {allocated} bytes for 5000 ({allocated / 5000} bytes/op)");
            return 0;
        }

        private static int MeasureInjectAfterClear()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            var instance = new CmdOnlyCommand();
            container.Inject(instance);

            // Warm both caches and simulate the pooled lifecycle: clear, then inject.
            for (int i = 0; i < 100; i++)
            {
                NexusDI.ClearInjectedReferences(instance);
                container.Inject(instance);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                NexusDI.ClearInjectedReferences(instance);
                container.Inject(instance);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine($"[Nexus Benchmark] Clear+Inject cycle (pooled state): {allocated} bytes for 5000 ({allocated / 5000} bytes/op)");
            return 0;
        }

        private static int MeasureFireNoInjectCommand()
        {
            var container = new NexusDI();
            container.Bind<NoInjectCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(NoInjectCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                for (int i = 0; i < 100; i++) bus.Fire(new CmdSignal(i));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new CmdSignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                long perDispatch = allocated / 5000;
                Console.WriteLine($"[Nexus Benchmark] fire-with-no-inject-command: allocated={allocated} bytes for 5000 ({perDispatch} bytes/dispatch) executions={NoInjectCommand.Executions}");
                return 0;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }

        private static async System.Threading.Tasks.Task<int> MeasureFireAsyncSyncCommand()
        {
            var container = new NexusDI();
            container.Bind<NoInjectCommand>(isSingleton: false);
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            // Registered as ASYNC so FireAsync routes through ExecuteAsync<TSignal>'s
            // sync-command branch (the path whose inline lambda previously allocated a
            // closure per dispatch). NoInjectCommand implements only ICommand<TSignal>,
            // so it hits that branch even though it executes synchronously.
            bus.RegisterCommand(typeof(CmdSignal), typeof(NoInjectCommand), ExecutionMode.Sequential, 0, isAsync: true);

            try
            {
                // Warm up (async state machines, pool, registry caches).
                for (int i = 0; i < 100; i++) await bus.FireAsync(new CmdSignal(i));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) await bus.FireAsync(new CmdSignal(i));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

                long perDispatch = allocated / 5000;
                // Threshold rationale (calibrated on net10 x64 reference): the async depth
                // guard now uses a mutable boxed holder (AsyncLocal<AsyncStackDepthBox>), so
                // the previous 96 B/op of AsyncLocal<int> boxing is gone — measured 192
                // B/dispatch (down from 288 with AsyncLocal<int>, and from 408 at HEAD before
                // the [NoInlining] closure fix). The residual 192 B is the async-path
                // baseline (state-machine/ExecutionContext machinery that remains even with
                // zero AsyncLocal traffic and zero closures). Limit 280 gives 88 B headroom
                // while still failing a regression to either prior shape: AsyncLocal<int>
                // reintroduced → ~288, closure hoisting reintroduced → ~312.
                Console.WriteLine($"[Nexus Benchmark] fire-async-with-sync-command: allocated={allocated} bytes for 5000 ({perDispatch} bytes/dispatch)");
                bool ok = perDispatch <= 280;
                Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  FireAsync sync-command allocation (limit <=280: fixed=192, AsyncLocal<int>=288, HEAD=408)");
                return ok ? 0 : 1;
            }
            finally
            {
                bus.Dispose();
                poolManager.Clear();
                container.Dispose();
            }
        }
    }
}
