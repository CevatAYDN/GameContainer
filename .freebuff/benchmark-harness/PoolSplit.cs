// Pool-split diagnostic: pin down the 32 bytes/op warm pool round-trip and the
// extra 56 bytes/dispatch in fire-with-command. Measures:
//  (p1) direct CommandPool.Get()/Return() round-trip (warm)
//  (p2) via CommandPoolManager (the real path)
//  (p3) Return-only (Get all first, hold, then Return all)
//  (p4) Get-only (warm, hold)
//  (p5) fire-with-command in 10 batches of 500 — uniform per-op or one lump?
//  (p6) ConcurrentDictionary.TryGetValue primitive
//  (p7) composition on the SAME pool: Get+Return / +Inject / +Execute / full fire
//
// Run: dotnet run -c Release -- --pool-split

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Core;

namespace NexusBench
{
    public static class PoolSplit
    {
        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === POOL SPLIT DIAGNOSTICS ===");

            int failures = 0;
            MeasureDirectPool();
            MeasureViaManager();
            MeasureReturnOnly();
            MeasureGetOnly();
            MeasureFireBatches();
            MeasureTryGetValuePrimitive();
            MeasureComposition();

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "[Nexus Benchmark] POOL SPLIT DONE"
                : $"[Nexus Benchmark] {failures} POOL SPLIT FAILED");
            return failures;
        }

        private static (NexusDI container, CommandPoolManager manager, CommandPool pool) Setup(bool preFill)
        {
            var container = new NexusDI();
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var manager = new CommandPoolManager(container, initialSize: preFill ? 100 : 4, maxSize: 100);
            var pool = new CommandPool(typeof(CmdOnlyCommand), () => container.Resolve(typeof(CmdOnlyCommand)), preFill ? 100 : 4, 100);
            return (container, manager, pool);
        }

        private static void MeasureDirectPool()
        {
            var (container, _, pool) = Setup(preFill: true);
            try
            {
                var w = pool.Get();
                pool.Return(w);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = pool.Get();
                    pool.Return(cmd);
                }
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Console.WriteLine($"[Nexus Benchmark] direct CommandPool.Get/Return (pre-filled 100): {alloc} bytes for 5000 ({alloc / 5000} bytes/op)");
            }
            finally
            {
                pool.Clear();
                container.Dispose();
            }
        }

        private static void MeasureViaManager()
        {
            var (container, manager, pool) = Setup(preFill: true);
            try
            {
                var w = manager.GetCommand(typeof(CmdOnlyCommand));
                manager.ReturnCommand(typeof(CmdOnlyCommand), w);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = manager.GetCommand(typeof(CmdOnlyCommand));
                    manager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Console.WriteLine($"[Nexus Benchmark] via CommandPoolManager (pre-filled 100): {alloc} bytes for 5000 ({alloc / 5000} bytes/op)");
            }
            finally
            {
                manager.Clear();
                container.Dispose();
            }
        }

        private static void MeasureReturnOnly()
        {
            var (container, manager, pool) = Setup(preFill: true);
            try
            {
                var held = new object[5000];
                for (int i = 0; i < 5000; i++) held[i] = manager.GetCommand(typeof(CmdOnlyCommand));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) manager.ReturnCommand(typeof(CmdOnlyCommand), held[i]);
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Console.WriteLine($"[Nexus Benchmark] ReturnCommand-only (5000 held, then return): {alloc} bytes for 5000 ({alloc / 5000} bytes/op)");
            }
            finally
            {
                manager.Clear();
                container.Dispose();
            }
        }

        private static void MeasureGetOnly()
        {
            var (container, manager, pool) = Setup(preFill: true);
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    var c = manager.GetCommand(typeof(CmdOnlyCommand));
                    manager.ReturnCommand(typeof(CmdOnlyCommand), c);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var held = new object[5000];
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) held[i] = manager.GetCommand(typeof(CmdOnlyCommand));
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Console.WriteLine($"[Nexus Benchmark] GetCommand-only (warm, holding 5000): {alloc} bytes for 5000 ({alloc / 5000} bytes/op)");
                for (int i = 0; i < 5000; i++) manager.ReturnCommand(typeof(CmdOnlyCommand), held[i]);
            }
            finally
            {
                manager.Clear();
                container.Dispose();
            }
        }

        private static void MeasureFireBatches()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var manager = new CommandPoolManager(container, initialSize: 100, maxSize: 100);
            var bus = new SignalBus(container, manager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(CmdOnlyCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                for (int i = 0; i < 200; i++) bus.Fire(new CmdSignal(i));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long prev = GC.GetAllocatedBytesForCurrentThread();
                long total = 0;
                for (int b = 0; b < 10; b++)
                {
                    for (int i = 0; i < 500; i++) bus.Fire(new CmdSignal(b * 500 + i));
                    long now = GC.GetAllocatedBytesForCurrentThread();
                    long batch = now - prev;
                    prev = now;
                    total += batch;
                    Console.WriteLine($"[Nexus Benchmark]   batch {b}: {batch} bytes for 500 ({batch / 500} bytes/dispatch)");
                }
                Console.WriteLine($"[Nexus Benchmark] fire-with-command (10x500 batches): total={total} ({total / 5000} bytes/dispatch) counter={counter.Value}");
            }
            finally
            {
                bus.Dispose();
                manager.Clear();
                container.Dispose();
            }
        }

        private static void MeasureTryGetValuePrimitive()
        {
            var dict = new ConcurrentDictionary<Type, object>();
            dict[typeof(CmdOnlyCommand)] = new object();

            for (int i = 0; i < 100; i++)
            {
                dict.TryGetValue(typeof(CmdOnlyCommand), out _);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                dict.TryGetValue(typeof(CmdOnlyCommand), out _);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine($"[Nexus Benchmark] ConcurrentDictionary.TryGetValue: {alloc} bytes for 5000 ({alloc / 5000} bytes/op)");
        }

        private static void MeasureComposition()
        {
            var counter = new CmdCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<CmdOnlyCommand>(isSingleton: false);
            var manager = new CommandPoolManager(container, initialSize: 100, maxSize: 100);
            var bus = new SignalBus(container, manager, new MockContext());
            bus.RegisterCommand(typeof(CmdSignal), typeof(CmdOnlyCommand), ExecutionMode.Sequential, 0, false);

            try
            {
                // Warm everything on the same bus/pool.
                for (int i = 0; i < 100; i++) bus.Fire(new CmdSignal(i));

                // (c1) Get+Return only.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long s1 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = manager.GetCommand(typeof(CmdOnlyCommand));
                    manager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long a1 = GC.GetAllocatedBytesForCurrentThread() - s1;

                // (c2) Get+Inject+Return.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long s2 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = manager.GetCommand(typeof(CmdOnlyCommand));
                    container.Inject(cmd);
                    manager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long a2 = GC.GetAllocatedBytesForCurrentThread() - s2;

                // (c3) Get+Inject+Execute+Return (manual ExecuteCommand body).
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long s3 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    var cmd = manager.GetCommand(typeof(CmdOnlyCommand));
                    container.Inject(cmd);
                    ((ICommand<CmdSignal>)cmd).Execute(new CmdSignal(i));
                    manager.ReturnCommand(typeof(CmdOnlyCommand), cmd);
                }
                long a3 = GC.GetAllocatedBytesForCurrentThread() - s3;

                // (c4) Full fire.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long s4 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) bus.Fire(new CmdSignal(i));
                long a4 = GC.GetAllocatedBytesForCurrentThread() - s4;

                Console.WriteLine($"[Nexus Benchmark] composition c1 Get+Return: {a1} ({a1 / 5000}/op)");
                Console.WriteLine($"[Nexus Benchmark] composition c2 +Inject:     {a2} ({a2 / 5000}/op)");
                Console.WriteLine($"[Nexus Benchmark] composition c3 +Execute:    {a3} ({a3 / 5000}/op)");
                Console.WriteLine($"[Nexus Benchmark] composition c4 full fire:   {a4} ({a4 / 5000}/op)");
            }
            finally
            {
                bus.Dispose();
                manager.Clear();
                container.Dispose();
            }
        }
    }
}
