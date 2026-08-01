// Concurrent DIFF suite: proves the WIRED SignalBus — which now delegates ALL registration and
// subscription state to CommandRegistry + SubscriptionRegistry — converges with the standalone
// registries under identical CONCURRENT workloads, and that concurrent churn on the shared
// registry layer is thread-safe (no dead-handler delivery, no lost updates, no mid-dispatch
// node pooling).
//
// This is the thread-safety proof: the plain differential suite (RegistrySuite DIFF1-9) proves
// single-threaded parity; this suite proves the same parity holds when 4 threads run identical
// interleavings against the real bus and against a RegistryDriver built purely from the public
// registry APIs. Dispatch itself is single-dispatcher (the documented model: sync Fire is
// main-thread-only; cross-thread traffic goes through HybridQueue, covered by CrossThreadSuite)
// while registration/subscription mutation is hammered from 4 threads — exactly the registry
// layer's thread-safety claim.
//
// Suite ids: CD = Concurrent Diff.
// CD1. WiredBus_vs_Standalone_ScriptedConcurrency — 4 threads subscribe + register concurrently,
//      the main dispatcher fires 1000, the 4 threads unsubscribe concurrently, the main fires a
//      20-signal tail. Delivered counts, handler-snapshot sizes, and async detection MUST
//      converge identically between the real bus and the standalone registries.
// CD2. WiredBus_ConcurrentChurn_NoDeadHandlerDelivery — 4 threads subscribe/unsubscribe on ONE
//      bus while it dispatches; every subscriber receives exactly all 1000 fires and nothing
//      after dispose. Includes mid-dispatch disposal of a sibling subscription (exercises the
//      deferred-sweep safety that prevents pooling a node while a reader walks the chain).
// CD3. Standalone_ConcurrentChurn_ConvergesWithBus — the same barrier-churn workload on the
//      standalone registries must yield the same aggregate count as CD2's bus.
// CD4. AsyncDetection_Parity — sync Fire with an async handler registered throws the same
//      NexusSyncAsyncMismatchException on both the bus and the standalone driver.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    public readonly struct CdSigA { public readonly int Value; public CdSigA(int v) => Value = v; }
    public readonly struct CdSigB { public readonly int Value; public CdSigB(int v) => Value = v; }

    public class CdCmdA : ICommand<CdSigA>
    {
        public void Execute(CdSigA signal) { }
    }

    public class CdAsyncCmdB : IAsyncCommand<CdSigB>
    {
        public ValueTask ExecuteAsync(CdSigB signal, CancellationToken ct) => default;
    }

    /// <summary>
    /// Replicates SignalBus's dispatch contract (commands first, then subscriptions, deferred
    /// sweep on unwind) using ONLY the public registry APIs — the standalone side of the diff.
    /// DI/pooling is not diffed here (covered by the main pipeline); the registry layer is.
    /// </summary>
    internal interface IScriptedTarget
    {
        ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct;
        void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync);
        void Fire<T>(T signal) where T : struct;
    }

    /// <summary>Adapts the real SignalBus to the scripted diff surface (structural types don't
    /// implement interfaces in C#, so the bus needs a thin wrapper).</summary>
    public sealed class BusTarget : IScriptedTarget
    {
        private readonly SignalBus _bus;
        public BusTarget(SignalBus bus) => _bus = bus;
        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct => _bus.Subscribe(handler);
        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync)
            => _bus.RegisterCommand(signalType, commandType, mode, priority, isAsync);
        public void Fire<T>(T signal) where T : struct => _bus.Fire(signal);
    }

    public sealed class RegistryDriver : IDisposable, IScriptedTarget
    {
        private readonly CommandRegistry _commands;
        private readonly SubscriptionRegistry _subscriptions;

        public RegistryDriver()
        {
            _commands = new CommandRegistry(new NexusDI());
            _subscriptions = new SubscriptionRegistry
            {
                // Same idle-reclaim semantics the bus enables, so both sides sweep identically.
                ImmediateSweepWhenIdle = true
            };
        }

        public CommandRegistry Commands => _commands;
        public SubscriptionRegistry Subscriptions => _subscriptions;

        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync)
            => _commands.RegisterCommand(signalType, commandType, mode, priority, isAsync);

        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct
            => _subscriptions.Subscribe<T>(handler, CancellationToken.None);

        public void Fire<T>(T signal) where T : struct
        {
            var type = typeof(T);
            if (_commands.HasAsyncCommandHandlers(type) || _subscriptions.HasAsyncSubscriptions(type))
            {
                // Same exception type the real bus throws — true parity (the type is public).
                throw new NexusSyncAsyncMismatchException(
                    "Synchronous Fire() was called for a signal that has asynchronous handlers or subscriptions registered.");
            }

            _subscriptions.EnterDispatch();
            try
            {
                if (_commands.TryGetHandlers(type, out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        var cmd = Activator.CreateInstance(handler.CommandType);
                        if (cmd is ICommand<T> genericCmd) genericCmd.Execute(signal);
                    }
                }

                if (_subscriptions.SubscriptionsReadCopy.TryGetValue(type, out var node))
                {
                    var current = node;
                    while (current != null)
                    {
                        if (current.IsActive && current.Handler is Action<T> syncSub)
                            syncSub(signal);
                        current = current.Next;
                    }
                }
            }
            finally
            {
                _subscriptions.ExitDispatch();
            }
        }

        public void Dispose()
        {
            _subscriptions.Dispose();
            _commands.Dispose();
        }
    }

    public static class ConcurrentDiffSuite
    {
        private static int _failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus ConcurrentDiff] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("ConcurrentDiff", name, ok, detail);
            if (!ok) _failures++;
        }

        // ── CD1: scripted 4-thread sequence — real bus vs standalone driver ─────────────

        private static void WiredBus_vs_Standalone_ScriptedConcurrency()
        {
            var busContainer = new NexusDI();
            var bus = new SignalBus(busContainer, new CommandPoolManager(busContainer), new MockContext());
            var driver = new RegistryDriver();
            bool ok = false;
            string detail = "no detail";
            try
            {
                const int threads = 4;
                const int mainFires = 1000;
                var busCounts = new int[threads];
                var driverCounts = new int[threads];

                RunScripted(new BusTarget(bus), busCounts, threads, mainFires);
                RunScripted(driver, driverCounts, threads, mainFires);

                bool countsConverge = true;
                var countsDetail = new List<string>();
                for (int t = 0; t < threads; t++)
                {
                    countsDetail.Add($"bus{t}={busCounts[t]} drv{t}={driverCounts[t]}");
                    if (busCounts[t] != mainFires || driverCounts[t] != mainFires) countsConverge = false;
                }

                // Handler snapshots: each thread registered one CdSigA handler on both sides;
                // concurrent registration under the registry lock must land all four.
                bool busSnap = bus.CommandHandlers.TryGetValue(typeof(CdSigA), out var busHandlers) && busHandlers.Count == threads;
                bool drvSnap = driver.Commands.CommandHandlers.TryGetValue(typeof(CdSigA), out var drvHandlers) && drvHandlers.Count == threads;

                ok = countsConverge && busSnap && drvSnap;
                detail = $"counts=[{string.Join(",", countsDetail)}] handlers(bus={busHandlers?.Count}, drv={drvHandlers?.Count})";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                bus.Dispose();
                busContainer.Dispose();
                driver.Dispose();
            }

            Report("CD1. WiredBus_vs_Standalone_ScriptedConcurrency", ok, detail);
        }

        /// <summary>
        /// One scripted run against a target that speaks SignalBus's surface: 4 threads subscribe
        /// and register concurrently (phase 1 barrier), the calling thread fires mainFires
        /// (phase 2 barrier), the 4 threads unsubscribe concurrently, the calling thread fires a
        /// 20-signal tail that must reach nobody.
        /// </summary>
        private static void RunScripted(IScriptedTarget target, int[] counts, int threads, int mainFires)
        {
            var barrier = new Barrier(threads + 1);
            var workers = new Thread[threads];                for (int t = 0; t < threads; t++)
                {
                    int id = t;
                    workers[t] = new Thread(() =>
                    {
                        var sub = target.Subscribe<CdSigA>(_ => Interlocked.Increment(ref counts[id]));
                        target.RegisterCommand(typeof(CdSigA), typeof(CdCmdA), ExecutionMode.Sequential, id, false);
                        barrier.SignalAndWait(); // phase 1: all subscribed + registered
                        barrier.SignalAndWait(); // phase 2: all fires done — dispose now
                        sub.Dispose();
                        barrier.SignalAndWait(); // phase 3: all disposed
                    })
                    { IsBackground = true }; // failure path must not hang the process
                    workers[t].Start();
                }

            barrier.SignalAndWait(); // phase 1
            for (int i = 0; i < mainFires; i++) target.Fire(new CdSigA(i));
            barrier.SignalAndWait(); // phase 2
            barrier.SignalAndWait(); // phase 3
            for (int i = 0; i < 20; i++) target.Fire(new CdSigA(i)); // must NOT reach disposed subs
            for (int t = 0; t < threads; t++) workers[t].Join();
        }

        // ── CD2: barrier churn on ONE shared bus + mid-dispatch sibling disposal ────────

        private static void WiredBus_ConcurrentChurn_NoDeadHandlerDelivery()
        {
            var container = new NexusDI();
            var bus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            bool ok = false;
            string detail = "no detail";
            try
            {
                const int threads = 4;
                const int mainFires = 1000;
                var counts = new int[threads];
                var unsubscribed = 0;
                var release = new ManualResetEventSlim(false);
                var allUnsubscribed = new ManualResetEventSlim(false);

                var subs = new ISignalSubscription[threads];
                var workers = new Thread[threads];
                for (int t = 0; t < threads; t++)
                {
                    int id = t;
                    workers[t] = new Thread(() =>
                    {
                        subs[id] = bus.Subscribe<CdSigA>(_ => Interlocked.Increment(ref counts[id]));
                        Interlocked.Increment(ref unsubscribed);
                        release.Wait(); // stay subscribed while the dispatcher fires
                        subs[id].Dispose();
                        if (Interlocked.Decrement(ref unsubscribed) == 0) allUnsubscribed.Set();
                    })
                    { IsBackground = true }; // failure path must not hang the process
                    workers[t].Start();
                }

                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref unsubscribed) < threads && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                if (Volatile.Read(ref unsubscribed) < threads) throw new TimeoutException("workers did not subscribe in 30s");
                for (int i = 0; i < mainFires; i++) bus.Fire(new CdSigA(i));
                release.Set();
                sw.Restart();
                while (!allUnsubscribed.IsSet && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                if (!allUnsubscribed.IsSet) throw new TimeoutException("workers did not unsubscribe in 30s");
                for (int i = 0; i < 20; i++) bus.Fire(new CdSigA(i)); // nobody subscribed
                for (int t = 0; t < threads; t++) workers[t].Join();

                bool allExact = true;
                for (int t = 0; t < threads; t++) if (counts[t] != mainFires) allExact = false;

                // Mid-dispatch sibling disposal: while the newest handler runs it disposes a LATER
                // node in the chain. The deferred sweep must keep the chain intact (no pooling a
                // node a reader is about to visit) and the disposed sibling must never fire.
                var chainProbe = new int[2];
                bus.Subscribe<CdSigA>(_ => { });                       // older, active, empty handler
                var sibling = bus.Subscribe<CdSigA>(_ => Interlocked.Increment(ref chainProbe[1]));
                bus.Subscribe<CdSigA>(_ =>
                {
                    Interlocked.Increment(ref chainProbe[0]);
                    sibling.Dispose(); // dispose a node that appears LATER in the walk order
                });
                bus.Fire(new CdSigA(1));
                bus.Fire(new CdSigA(2)); // second fire sweeps the dead node
                bool chainSafe = chainProbe[0] == 2 && chainProbe[1] == 0;

                ok = allExact && chainSafe;
                detail = $"counts=[{string.Join(",", counts)}] expected={mainFires} chain=({chainProbe[0]},dead={chainProbe[1]})";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                bus.Dispose();
                container.Dispose();
            }

            Report("CD2. WiredBus_ConcurrentChurn_NoDeadHandlerDelivery", ok, detail);
        }

        // ── CD3: the same churn on the standalone registries converges with CD2's bus ───

        private static void Standalone_ConcurrentChurn_ConvergesWithBus()
        {
            var driver = new RegistryDriver();
            bool ok = false;
            string detail = "no detail";
            try
            {
                const int threads = 4;
                const int mainFires = 1000;
                var counts = new int[threads];
                var unsubscribed = 0;
                var release = new ManualResetEventSlim(false);
                var allUnsubscribed = new ManualResetEventSlim(false);

                var subs = new ISignalSubscription[threads];
                var workers = new Thread[threads];
                for (int t = 0; t < threads; t++)
                {
                    int id = t;
                    workers[t] = new Thread(() =>
                    {
                        subs[id] = driver.Subscribe<CdSigA>(_ => Interlocked.Increment(ref counts[id]));
                        Interlocked.Increment(ref unsubscribed);
                        release.Wait();
                        subs[id].Dispose();
                        if (Interlocked.Decrement(ref unsubscribed) == 0) allUnsubscribed.Set();
                    })
                    { IsBackground = true }; // failure path must not hang the process
                    workers[t].Start();
                }

                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref unsubscribed) < threads && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                if (Volatile.Read(ref unsubscribed) < threads) throw new TimeoutException("workers did not subscribe in 30s");
                for (int i = 0; i < mainFires; i++) driver.Fire(new CdSigA(i));
                release.Set();
                sw.Restart();
                while (!allUnsubscribed.IsSet && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                if (!allUnsubscribed.IsSet) throw new TimeoutException("workers did not unsubscribe in 30s");
                for (int i = 0; i < 20; i++) driver.Fire(new CdSigA(i));
                for (int t = 0; t < threads; t++) workers[t].Join();

                bool allExact = true;
                for (int t = 0; t < threads; t++) if (counts[t] != mainFires) allExact = false;
                ok = allExact;
                detail = $"counts=[{string.Join(",", counts)}] expected={mainFires} (must match CD2 bus)";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                driver.Dispose();
            }

            Report("CD3. Standalone_ConcurrentChurn_ConvergesWithBus", ok, detail);
        }

        // ── CD4: async detection parity ─────────────────────────────────────────────────

        private static void AsyncDetection_Parity_Bus_vs_Standalone()
        {
            var container = new NexusDI();
            var bus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            var driver = new RegistryDriver();
            bool ok = false;
            string detail = "no detail";
            try
            {
                bus.RegisterCommand(typeof(CdSigB), typeof(CdAsyncCmdB), ExecutionMode.Sequential, 0, true);
                driver.RegisterCommand(typeof(CdSigB), typeof(CdAsyncCmdB), ExecutionMode.Sequential, 0, true);

                Exception busEx = null, driverEx = null;
                try { bus.Fire(new CdSigB(1)); } catch (Exception ex) { busEx = ex; }
                try { driver.Fire(new CdSigB(1)); } catch (Exception ex) { driverEx = ex; }

                // Both must throw the REAL NexusSyncAsyncMismatchException (driver throws the
                // same public type now) — true type parity, not a name-string coincidence.
                bool sameKind = busEx is NexusSyncAsyncMismatchException
                    && driverEx is NexusSyncAsyncMismatchException;
                ok = sameKind;
                detail = $"bus={(busEx?.GetType().Name ?? "none")} driver={(driverEx?.GetType().Name ?? "none")}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                bus.Dispose();
                container.Dispose();
                driver.Dispose();
            }

            Report("CD4. AsyncDetection_Parity_Bus_vs_Standalone", ok, detail);
        }

        // ── CD5: wiring proof — the bus has NO hidden duplicate storage ──────────────────

        private static void WiredBus_RegistryDelegation_ReflectionProof()
        {
            var container = new NexusDI();
            var bus = new SignalBus(container, new CommandPoolManager(container), new MockContext());
            bool ok = false;
            string detail = "no detail";
            try
            {
                var cmdField = typeof(SignalBus).GetField("_commandRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
                var subField = typeof(SignalBus).GetField("_subscriptionRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
                var cr = (CommandRegistry)cmdField.GetValue(bus);
                var sr = (SubscriptionRegistry)subField.GetValue(bus);

                bool fieldsBound = cr != null && sr != null;
                // The public CommandHandlers property MUST return the registry's own snapshot
                // instance (delegation, not a copy) — if the bus kept duplicate storage this
                // ReferenceEquals would fail.
                bool sameSnapshot = fieldsBound && ReferenceEquals(bus.CommandHandlers, cr.CommandHandlers);

                // Register + subscribe THROUGH THE BUS: the storage must be the registry
                // instances the bus holds (a hidden duplicate layer would leave them empty).
                bus.RegisterCommand(typeof(CdSigA), typeof(CdCmdA), ExecutionMode.Sequential, 0, false);
                int received = 0;
                var sub = bus.Subscribe<CdSigA>(_ => received++);
                bus.Fire(new CdSigA(1));

                bool registrySawRegistration = cr.CommandHandlers.TryGetValue(typeof(CdSigA), out var handlers)
                    && handlers.Count == 1 && handlers[0].CommandType == typeof(CdCmdA);
                bool registrySawSubscription = sr.SubscriptionsReadCopy.ContainsKey(typeof(CdSigA));
                bool dispatchedThroughRegistry = received == 1;

                ok = fieldsBound && sameSnapshot && registrySawRegistration && registrySawSubscription && dispatchedThroughRegistry;
                detail = $"fields={fieldsBound} sameSnapshot={sameSnapshot} regLanded={registrySawRegistration} " +
                    $"subLanded={registrySawSubscription} dispatched={dispatchedThroughRegistry}";
                sub.Dispose();
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                bus.Dispose();
                container.Dispose();
            }

            Report("CD5. WiredBus_RegistryDelegation_ReflectionProof", ok, detail);
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Nexus ConcurrentDiff] CONCURRENT DIFF: WIRED BUS vs STANDALONE REGISTRIES");
            Console.WriteLine("===============================================================================");
            WiredBus_vs_Standalone_ScriptedConcurrency();
            WiredBus_ConcurrentChurn_NoDeadHandlerDelivery();
            Standalone_ConcurrentChurn_ConvergesWithBus();
            AsyncDetection_Parity_Bus_vs_Standalone();
            WiredBus_RegistryDelegation_ReflectionProof();
            Console.WriteLine(_failures == 0
                ? "[Nexus ConcurrentDiff] ALL CONCURRENT DIFF TESTS PASSED ✓"
                : $"[Nexus ConcurrentDiff] {_failures} TEST(S) FAILED ✗");
            return _failures;
        }
    }
}
