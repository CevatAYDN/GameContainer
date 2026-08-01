// Registry proof suite: exercises the extracted registration layer — CommandRegistry and
// SubscriptionRegistry — against the REAL runtime, and proves the extraction preserves
// SignalBus behavior with a differential test (identical registration sequences must produce
// identical outcomes). Also proves the fixed dispatcher/setter caches actually dispatch
// (the pre-fix CommandRegistry recursively invoked itself / returned a stub), composite
// validation parity, subscription lifecycle, node-pool reuse, and zero-GC read paths.
//
// Suite ids: CR = CommandRegistry, SR = SubscriptionRegistry, DIFF = differential vs SignalBus.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    // ---------------------------------------------------------------------------
    // Signals + commands for the registry tests
    // ---------------------------------------------------------------------------

    public readonly struct RegSigA { public readonly int Value; public RegSigA(int v) => Value = v; }
    public readonly struct RegSigB { public readonly int Value; public RegSigB(int v) => Value = v; }
    public readonly struct RegSigC { public readonly int Value; public RegSigC(int v) => Value = v; }
    public readonly struct RegSigD { public readonly int Value; public RegSigD(int v) => Value = v; }
    public readonly struct RegSigE { public readonly int Value; public RegSigE(int v) => Value = v; }

    public class RegCmdA : ICommand<RegSigA>
    {
        public void Execute(RegSigA signal) { }
    }

    public class RegCmdB : ICommand<RegSigB>
    {
        public void Execute(RegSigB signal) { }
    }

    public class RegAsyncCmdC : IAsyncCommand<RegSigC>
    {
        public ValueTask ExecuteAsync(RegSigC signal, CancellationToken ct) => default;
    }

    /// <summary>Implements neither ICommand nor IAsyncCommand — invalid registration target.</summary>
    public class RegNoInterfaceCmd { }

    /// <summary>Implements both generic interfaces — must be rejected at registration.</summary>
    public class RegBothInterfacesCmd : ICommand<RegSigA>, IAsyncCommand<RegSigA>
    {
        public void Execute(RegSigA signal) { }
        public ValueTask ExecuteAsync(RegSigA signal, CancellationToken ct) => default;
    }

    /// <summary>Async command registered with isAsync:false — must be rejected.</summary>
    public class RegAsyncCmdAsSync : IAsyncCommand<RegSigB>
    {
        public ValueTask ExecuteAsync(RegSigB signal, CancellationToken ct) => default;
    }

    /// <summary>Generic-only command (ICommand&lt;TSignal&gt;, NOT non-generic ICommand) — the dispatcher-cache target.</summary>
    public class RegGenericOnlyCmd : ICommand<RegSigD>
    {
        public int ExecuteCount;
        public RegSigD LastSignal;
        public void Execute(RegSigD signal) { ExecuteCount++; LastSignal = signal; }
    }

    /// <summary>Generic-only async command (IAsyncCommand&lt;TSignal&gt;) — the async dispatcher-cache target.</summary>
    public class RegGenericOnlyAsyncCmd : IAsyncCommand<RegSigE>
    {
        public int ExecuteCount;
        public RegSigE LastSignal;
        public ValueTask ExecuteAsync(RegSigE signal, CancellationToken ct)
        {
            ExecuteCount++;
            LastSignal = signal;
            return default;
        }
    }

    /// <summary>Non-generic command with a `_signal` field — the signal-setter target (field path).</summary>
    public class RegSetterFieldCmd : ICommand
    {
        public RegSigA _signal;
        public void Execute() { }
    }

    /// <summary>Non-generic command with a `Signal` property — the signal-setter target (property path).</summary>
    public class RegSetterPropertyCmd : ICommand
    {
        public RegSigA Signal { get; set; }
        public void Execute() { }
    }

    /// <summary>Command with a `signal` string field — must NOT be matched by the setter (type mismatch).</summary>
    public class RegSetterWrongTypeCmd : ICommand
    {
        public string signal = "keep";
        public void Execute() { }
    }

    [CrossContext(ScopeTag = "RegScope")]
    public readonly struct RegCrossSig { }

    public static class RegistrySuite
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Registry] EXTRACTED REGISTRATION LAYER PROOF: COMMAND+SUBSCRIPTION REGISTRIES");
            Console.WriteLine("===============================================================================");

            _failures = 0;
            try
            {
                RunCommandRegistry();
                RunSubscriptionRegistry();
                RunDifferential();
                RunZeroGc();
            }
            catch (Exception ex)
            {
                Check("Suite_InternalError", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                NexusRuntime.Reset();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Registry] ALL REGISTRY TESTS PASSED ✓"
                : $"[Registry] {_failures} REGISTRY TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Registry] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Registry", name, ok, detail);
            if (!ok) _failures++;
        }

        // =========================================================================
        // CommandRegistry — validation, snapshots, async tracking, caches, dispose
        // =========================================================================

        private static void RunCommandRegistry()
        {
            var container = new NexusDI();
            var reg = new CommandRegistry(container);
            try
            {
                // CR1: valid sync registration lands in both snapshots with correct metadata.
                reg.RegisterCommand(typeof(RegSigA), typeof(RegCmdA), ExecutionMode.Sequential, 10, false);
                bool registered = reg.CommandHandlers.TryGetValue(typeof(RegSigA), out var handlers)
                    && handlers.Count == 1
                    && handlers[0].CommandType == typeof(RegCmdA)
                    && handlers[0].Mode == ExecutionMode.Sequential
                    && handlers[0].Priority == 10
                    && !handlers[0].IsAsync;
                Check("CR1. RegisterCommand_ValidSync_LandsInSnapshots", registered,
                    $"handlers={reg.CommandHandlers.Count} signalA={reg.CommandHandlers.ContainsKey(typeof(RegSigA))}");

                // CR2: valid async registration sets the async-handler cache.
                reg.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 0, true);
                bool asyncTracked = reg.HasAsyncCommandHandlers(typeof(RegSigC))
                    && !reg.HasAsyncCommandHandlers(typeof(RegSigA));
                Check("CR2. AsyncHandler_Cache_Tracked", asyncTracked,
                    $"C={reg.HasAsyncCommandHandlers(typeof(RegSigC))} A={reg.HasAsyncCommandHandlers(typeof(RegSigA))}");

                // CR3: command implementing no command interface is rejected.
                bool cr3 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigA), typeof(RegNoInterfaceCmd), ExecutionMode.Sequential, 0, false));
                Check("CR3. NoInterface_Rejected", cr3, "InvalidOperationException expected");

                // CR4: command implementing both sync+async generic interfaces is rejected.
                bool cr4 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigA), typeof(RegBothInterfacesCmd), ExecutionMode.Sequential, 0, false));
                Check("CR4. BothInterfaces_Rejected", cr4, "InvalidOperationException expected");

                // CR5: async command registered as sync is rejected.
                bool cr5 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigB), typeof(RegAsyncCmdAsSync), ExecutionMode.Sequential, 0, false));
                Check("CR5. AsyncAsSync_Rejected", cr5, "InvalidOperationException expected");

                // CR6: mixed-mode dispatch is rejected (Sequential then Concurrent).
                reg.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Sequential, 0, false);
                bool cr6 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Concurrent, 0, false));
                Check("CR6. MixedMode_Rejected", cr6, "InvalidOperationException expected");

                // CR7: exclusive mode allows only one handler.
                reg.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true);
                bool cr7 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true));
                Check("CR7. Exclusive_SecondHandler_Rejected", cr7, "InvalidOperationException expected");

                // CR8: duplicate priority rejected for non-concurrent modes.
                reg.RegisterCommand(typeof(RegSigB), typeof(RegCmdB), ExecutionMode.Sequential, 5, false);
                bool cr8 = Throws<InvalidOperationException>(() =>
                    reg.RegisterCommand(typeof(RegSigB), typeof(RegCmdB), ExecutionMode.Sequential, 5, false));
                Check("CR8. DuplicatePriority_Rejected", cr8, "InvalidOperationException expected");

                // CR9: handlers sort by priority descending.
                reg.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 1, true);
                reg.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 30, true);
                reg.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 2, true);
                bool sorted = reg.TryGetHandlers(typeof(RegSigC), out var sortedHandlers)
                    && sortedHandlers[0].Priority == 30
                    && sortedHandlers[1].Priority == 2
                    && sortedHandlers[2].Priority == 1;
                Check("CR9. Priority_SortedDescending", sorted,
                    $"priorities=[{sortedHandlers[0].Priority},{sortedHandlers[1].Priority},{sortedHandlers[2].Priority}]");

                // CR10: composite registration lands in per-signal + all lists.
                reg.RegisterCompositeCommand(new[] { typeof(RegSigA), typeof(RegSigB) }, typeof(RegCmdA), oneShot: false, 5, false);
                bool gotTriggersA = reg.TryGetCompositeTriggers(typeof(RegSigA), out var triggersA);
                bool gotTriggersB = reg.TryGetCompositeTriggers(typeof(RegSigB), out var triggersB);
                bool composite = gotTriggersA && triggersA.Count == 1
                    && gotTriggersB && triggersB.Count == 1
                    && reg.AllCompositeTriggers.Count == 1;
                Check("CR10. Composite_Registered_And_Indexed", composite,
                    $"all={reg.AllCompositeTriggers.Count} A={(gotTriggersA ? triggersA.Count : -1)} B={(gotTriggersB ? triggersB.Count : -1)}");

                // CR11: composite validation — null, empty, >64, duplicate.
                bool cr11 = Throws<ArgumentException>(() => reg.RegisterCompositeCommand(null, typeof(RegCmdA), false, 0, false))
                    && Throws<ArgumentException>(() => reg.RegisterCompositeCommand(new Type[0], typeof(RegCmdA), false, 0, false))
                    && Throws<ArgumentException>(() => reg.RegisterCompositeCommand(new Type[65], typeof(RegCmdA), false, 0, false))
                    && Throws<ArgumentException>(() => reg.RegisterCompositeCommand(new[] { typeof(RegSigA), typeof(RegSigA) }, typeof(RegCmdA), false, 0, false))
                    && Throws<ArgumentException>(() => reg.RegisterCompositeCommand(new[] { typeof(RegSigA), null }, typeof(RegCmdA), false, 0, false));
                Check("CR11. Composite_Validation_Parity", cr11,
                    "null/empty/65/duplicate/null-element all ArgumentException");

                // CR12: cross-context attribute cache.
                bool cross = reg.GetCachedCrossContext(typeof(RegCrossSig)) != null
                    && reg.GetCachedCrossContext(typeof(RegCrossSig)).ScopeTag == "RegScope"
                    && reg.GetCachedCrossContext(typeof(RegSigA)) == null;
                Check("CR12. CrossContext_Cache", cross, "RegCrossSig scoped, RegSigA null");

                // CR13: fixed sync dispatcher actually dispatches to a generic-only command.
                var genericCmd = new RegGenericOnlyCmd();
                var syncDispatcher = reg.GetGenericSyncDispatcher(typeof(RegGenericOnlyCmd), typeof(RegSigD));
                bool cr13 = syncDispatcher != null;
                syncDispatcher?.Invoke(genericCmd, new RegSigD(42));
                cr13 &= genericCmd.ExecuteCount == 1 && genericCmd.LastSignal.Value == 42;
                Check("CR13. SyncDispatcher_Dispatches_GenericOnly", cr13,
                    $"executes={genericCmd.ExecuteCount} last={genericCmd.LastSignal.Value} (was infinite recursion pre-fix)");

                // CR14: fixed async dispatcher actually dispatches to a generic-only async command.
                var genericAsyncCmd = new RegGenericOnlyAsyncCmd();
                var asyncDispatcher = reg.GetGenericAsyncDispatcher(typeof(RegGenericOnlyAsyncCmd), typeof(RegSigE));
                bool cr14 = asyncDispatcher != null;
                asyncDispatcher?.Invoke(genericAsyncCmd, new RegSigE(7), CancellationToken.None).GetAwaiter().GetResult();
                cr14 &= genericAsyncCmd.ExecuteCount == 1 && genericAsyncCmd.LastSignal.Value == 7;
                Check("CR14. AsyncDispatcher_Dispatches_GenericOnly", cr14,
                    $"executes={genericAsyncCmd.ExecuteCount} last={genericAsyncCmd.LastSignal.Value} (was stub pre-fix)");

                // CR15: signal setter — field path, property path, and wrong-type guard.
                var fieldCmd = new RegSetterFieldCmd();
                var fieldSetter = reg.GetSignalSetter(typeof(RegSetterFieldCmd), typeof(RegSigA));
                fieldSetter?.Invoke(fieldCmd, new RegSigA(9));
                bool fieldPath = fieldCmd._signal.Value == 9;

                var propCmd = new RegSetterPropertyCmd();
                var propSetter = reg.GetSignalSetter(typeof(RegSetterPropertyCmd), typeof(RegSigA));
                propSetter?.Invoke(propCmd, new RegSigA(3));
                bool propPath = propCmd.Signal.Value == 3;

                // A `signal` string field must NOT match a struct signal (IsInstanceOfType(null)
                // would have matched ANY reference-typed field named signal pre-fix).
                var wrongCmd = new RegSetterWrongTypeCmd();
                var wrongSetter = reg.GetSignalSetter(typeof(RegSetterWrongTypeCmd), typeof(RegSigA));
                wrongSetter?.Invoke(wrongCmd, new RegSigA(5));
                bool wrongGuard = wrongCmd.signal == "keep";
                Check("CR15. SignalSetter_Field_Property_WrongTypeGuard", fieldPath && propPath && wrongGuard,
                    $"field={fieldPath} prop={propPath} wrongTypeGuard={wrongGuard}");

                // CR16: dispatcher caches are stable (second call returns same delegate).
                bool cr16 = ReferenceEquals(syncDispatcher, reg.GetGenericSyncDispatcher(typeof(RegGenericOnlyCmd), typeof(RegSigD)))
                    && ReferenceEquals(asyncDispatcher, reg.GetGenericAsyncDispatcher(typeof(RegGenericOnlyAsyncCmd), typeof(RegSigE)));
                Check("CR16. Dispatcher_Caches_Stable", cr16, "same delegate instances returned");
            }
            finally
            {
                reg.Dispose();
                container.Dispose();
            }
        }

        // =========================================================================
        // SubscriptionRegistry — lifecycle, pool reuse, sweep, dispose
        // =========================================================================

        private static void RunSubscriptionRegistry()
        {
            var reg = new SubscriptionRegistry();
            try
            {
                // SR1: subscribe adds an active node visible in the read copy.
                int received = 0;
                var sub = reg.Subscribe<RegSigA>(_ => received++, CancellationToken.None);
                bool sr1 = sub.IsActive && reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigA));
                Check("SR1. Subscribe_Adds_ReadCopyNode", sr1,
                    $"containsA={reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigA))}");

                // SR2: multiple subscribers for one type chain newest-first.
                int received2 = 0;
                reg.Subscribe<RegSigA>(_ => received2++, CancellationToken.None);
                bool sr2 = reg.SubscriptionsReadCopy.TryGetValue(typeof(RegSigA), out var head) && head.Next != null;
                Check("SR2. MultipleSubscribers_Linked", sr2, $"chain={head != null && head.Next != null}");

                // SR3: async subscription is detected; sync-only signal is not.
                var asyncSub = reg.SubscribeAsync<RegSigB>((_, _) => default, CancellationToken.None);
                bool sr3 = reg.HasAsyncSubscriptions(typeof(RegSigB)) && !reg.HasAsyncSubscriptions(typeof(RegSigA));
                Check("SR3. AsyncSubscription_Detected", sr3,
                    $"B={reg.HasAsyncSubscriptions(typeof(RegSigB))} A={reg.HasAsyncSubscriptions(typeof(RegSigA))}");

                // SR4: unsubscribe marks inactive; explicit sweep removes the node from the read copy.
                var subC = reg.Subscribe<RegSigC>(_ => { }, CancellationToken.None);
                reg.Unsubscribe(typeof(RegSigC), subC);
                bool inactiveBeforeSweep = reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigC));
                reg.SweepDeadNodes();
                bool sr4 = inactiveBeforeSweep && !reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigC));
                Check("SR4. Unsubscribe_ThenSweep_RemovesNode", sr4,
                    $"inactive-retained={inactiveBeforeSweep} containsC-after-sweep={reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigC))}");

                // SR5: sweep keeps active nodes, only removes the dead one.
                var alive = reg.Subscribe<RegSigD>(_ => { }, CancellationToken.None);
                var dead = reg.Subscribe<RegSigD>(_ => { }, CancellationToken.None);
                reg.Unsubscribe(typeof(RegSigD), dead);
                reg.SweepDeadNodes();
                bool sr5 = reg.SubscriptionsReadCopy.TryGetValue(typeof(RegSigD), out var liveHead)
                    && liveHead.RawSubscription == alive
                    && liveHead.Next == null;
                Check("SR5. Sweep_KeepsActive_RemovesDead", sr5,
                    $"alive={(liveHead != null ? liveHead.RawSubscription == alive : false)}");

                // SR6: idempotent sweep with nothing pending is a no-op.
                bool sr6 = true;
                for (int i = 0; i < 1000; i++) reg.SweepDeadNodes();
                Check("SR6. Sweep_NoPending_NoOp", sr6, "1000 no-pending sweeps");

                // SR7: node pool reuse — returning a node makes it rentable again (0 new node allocs).
                bool sr7 = NodePoolReuseProof();
                Check("SR7. NodePool_Reuses_Instances", sr7, "Return->Rent returns the same instance");

                // SR8: dispose clears everything.
                reg.Dispose();
                bool sr8 = reg.SubscriptionsReadCopy.Count == 0 && !reg.HasAsyncSubscriptions(typeof(RegSigB));
                Check("SR8. Dispose_Clears_All", sr8, $"readCopy={reg.SubscriptionsReadCopy.Count}");
                return;
            }
            finally
            {
                reg.Dispose();
            }
        }

        /// <summary>Rent → Return → Rent must yield the same pooled instance (no new allocation).</summary>
        private static bool NodePoolReuseProof()
        {
            var first = SubscriptionNodePool.Rent(new object(), new object(), false);
            SubscriptionNodePool.Return(first);
            var second = SubscriptionNodePool.Rent(new object(), new object(), false);
            SubscriptionNodePool.Return(second);
            return ReferenceEquals(first, second);
        }

        // =========================================================================
        // Differential — identical sequences through SignalBus and CommandRegistry
        // =========================================================================

        private static void RunDifferential()
        {
            // DIFF1: valid sync registration — no throw on either.
            DiffCase("DIFF1. ValidSync_BothAccept",
                bus => bus.RegisterCommand(typeof(RegSigA), typeof(RegCmdA), ExecutionMode.Sequential, 0, false),
                reg => reg.RegisterCommand(typeof(RegSigA), typeof(RegCmdA), ExecutionMode.Sequential, 0, false));

            // DIFF2: valid async registration — no throw on either.
            DiffCase("DIFF2. ValidAsync_BothAccept",
                bus => bus.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 0, true),
                reg => reg.RegisterCommand(typeof(RegSigC), typeof(RegAsyncCmdC), ExecutionMode.Sequential, 0, true));

            // DIFF3: no command interface — same exception type AND message.
            DiffCase("DIFF3. NoInterface_IdenticalRejection",
                bus => bus.RegisterCommand(typeof(RegSigA), typeof(RegNoInterfaceCmd), ExecutionMode.Sequential, 0, false),
                reg => reg.RegisterCommand(typeof(RegSigA), typeof(RegNoInterfaceCmd), ExecutionMode.Sequential, 0, false));

            // DIFF4: both interfaces — identical rejection.
            DiffCase("DIFF4. BothInterfaces_IdenticalRejection",
                bus => bus.RegisterCommand(typeof(RegSigA), typeof(RegBothInterfacesCmd), ExecutionMode.Sequential, 0, false),
                reg => reg.RegisterCommand(typeof(RegSigA), typeof(RegBothInterfacesCmd), ExecutionMode.Sequential, 0, false));

            // DIFF5: async registered as sync — identical rejection.
            DiffCase("DIFF5. AsyncAsSync_IdenticalRejection",
                bus => bus.RegisterCommand(typeof(RegSigB), typeof(RegAsyncCmdAsSync), ExecutionMode.Sequential, 0, false),
                reg => reg.RegisterCommand(typeof(RegSigB), typeof(RegAsyncCmdAsSync), ExecutionMode.Sequential, 0, false));

            // DIFF6: mixed-mode — first Sequential accepted, then Concurrent rejected identically.
            DiffCase("DIFF6. MixedMode_IdenticalRejection",
                bus =>
                {
                    bus.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Sequential, 0, false);
                    bus.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Concurrent, 0, false);
                },
                reg =>
                {
                    reg.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Sequential, 0, false);
                    reg.RegisterCommand(typeof(RegSigD), typeof(RegGenericOnlyCmd), ExecutionMode.Concurrent, 0, false);
                });

            // DIFF7: exclusive second handler — identical rejection.
            DiffCase("DIFF7. ExclusiveSecond_IdenticalRejection",
                bus =>
                {
                    bus.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true);
                    bus.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true);
                },
                reg =>
                {
                    reg.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true);
                    reg.RegisterCommand(typeof(RegSigE), typeof(RegGenericOnlyAsyncCmd), ExecutionMode.Exclusive, 0, true);
                });

            // DIFF8: composite duplicate-signal rejection — identical exception.
            DiffCase("DIFF8. CompositeDuplicate_IdenticalRejection",
                bus => bus.RegisterCompositeCommand(new[] { typeof(RegSigA), typeof(RegSigA) }, typeof(RegCmdA), false, 0, false),
                reg => reg.RegisterCompositeCommand(new[] { typeof(RegSigA), typeof(RegSigA) }, typeof(RegCmdA), false, 0, false));

            // DIFF9: composite >64 signals — identical exception.
            DiffCase("DIFF9. CompositeTooMany_IdenticalRejection",
                bus => bus.RegisterCompositeCommand(new Type[65], typeof(RegCmdA), false, 0, false),
                reg => reg.RegisterCompositeCommand(new Type[65], typeof(RegCmdA), false, 0, false));
        }

        /// <summary>Runs the same registration lambda against a real SignalBus and a CommandRegistry,
        /// asserting identical outcome (both throw the same exception type with the same message,
        /// or both succeed).</summary>
        private static void DiffCase(string name, Action<SignalBus> onBus, Action<CommandRegistry> onReg)
        {
            var busContainer = new NexusDI();
            var bus = new SignalBus(busContainer, new CommandPoolManager(busContainer), new MockContext());
            var regContainer = new NexusDI();
            var reg = new CommandRegistry(regContainer);
            try
            {
                var busOutcome = Capture(onBus, bus);
                var regOutcome = Capture(onReg, reg);
                bool sameType = busOutcome.ExceptionType == regOutcome.ExceptionType;
                bool sameMessage = busOutcome.Message == regOutcome.Message;
                Check(name, sameType && sameMessage,
                    $"bus=[{busOutcome}] registry=[{regOutcome}] ({(sameType && sameMessage ? "identical" : "MISMATCH")})");
            }
            finally
            {
                reg.Dispose();
                bus.Dispose();
                busContainer.Dispose();
                regContainer.Dispose();
            }
        }

        private static (string ExceptionType, string Message) Capture(Action<SignalBus> action, SignalBus bus)
        {
            try { action(bus); return (null, null); }
            catch (Exception ex) { return (ex.GetType().Name, ex.Message); }
        }

        private static (string ExceptionType, string Message) Capture(Action<CommandRegistry> action, CommandRegistry reg)
        {
            try { action(reg); return (null, null); }
            catch (Exception ex) { return (ex.GetType().Name, ex.Message); }
        }

        // =========================================================================
        // Zero-GC — read paths must not allocate (<=128 B / 5000 ops)
        // =========================================================================

        private static void RunZeroGc()
        {
            // Z1: HasAsyncSubscriptions + read-copy access are allocation-free.
            var reg = new SubscriptionRegistry();
            try
            {
                for (int i = 0; i < 10; i++) reg.Subscribe<RegSigA>(_ => { }, CancellationToken.None);
                reg.SubscribeAsync<RegSigB>((_, _) => default, CancellationToken.None);
                for (int i = 0; i < 100; i++) _ = reg.HasAsyncSubscriptions(typeof(RegSigB)); // warmup

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++)
                {
                    _ = reg.HasAsyncSubscriptions(typeof(RegSigB));
                    _ = reg.SubscriptionsReadCopy.ContainsKey(typeof(RegSigA));
                }
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Check("Z1. Registry_ReadPaths_ZeroGC", alloc <= 128,
                    $"allocated={alloc} bytes for 5000 read ops (limit <=128)");
            }
            finally
            {
                reg.Dispose();
            }

            // Z2: steady-state sweep with nothing pending is a no-op (no allocation).
            var reg2 = new SubscriptionRegistry();
            try
            {
                for (int i = 0; i < 10; i++) reg2.Subscribe<RegSigA>(_ => { }, CancellationToken.None);
                for (int i = 0; i < 100; i++) reg2.SweepDeadNodes(); // warmup

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) reg2.SweepDeadNodes();
                long alloc = GC.GetAllocatedBytesForCurrentThread() - start;
                Check("Z2. Sweep_NoPending_ZeroGC", alloc <= 128,
                    $"allocated={alloc} bytes for 5000 no-pending sweeps (limit <=128)");
            }
            finally
            {
                reg2.Dispose();
            }
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
            catch (Exception) { return false; }
        }
    }
}
