using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace NexusBench
{
    public struct DtSignal { }
    public struct DtConcurrentSignal { }
    public struct DtRuleSignal { }

    /// <summary>Shared state of the suite's commands; each test resets it.</summary>
    internal static class DtState
    {
        public static readonly List<string> Order = new List<string>();
        public static TaskCompletionSource<bool> Gate;
        public static TaskCompletionSource<bool> Finished;
        public static TaskCompletionSource<bool> Cancelled;
        public static int Runs;
        public static int ConcurrentStarted;

        public static void Reset()
        {
            lock (Order) Order.Clear();
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Runs = 0;
            ConcurrentStarted = 0;
        }

        public static void Record(string step) { lock (Order) Order.Add(step); }
    }

    public sealed class DtGatedDetachedCommand : IAsyncCommand<DtSignal>
    {
        public async ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct)
        {
            DtState.Record("detached");
            Interlocked.Increment(ref DtState.Runs);
            await DtState.Gate.Task;
            DtState.Finished.TrySetResult(true);
        }
    }

    public sealed class DtSequentialCommand : IAsyncCommand<DtSignal>
    {
        public ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct)
        {
            DtState.Record("sequential");
            return default;
        }
    }

    public sealed class DtThrowingDetachedCommand : IAsyncCommand<DtSignal>
    {
        public async ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("detached failure");
        }
    }

    public sealed class DtWaitingDetachedCommand : IAsyncCommand<DtSignal>
    {
        public async ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { DtState.Cancelled.TrySetResult(true); throw; }
        }
    }

    public sealed class DtHangingSequentialCommand : IAsyncCommand<DtSignal>
    {
        public async ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
    }

    public sealed class DtCountingDetachedCommand : IAsyncCommand<DtSignal>
    {
        public ValueTask ExecuteAsync(DtSignal signal, CancellationToken ct)
        {
            Interlocked.Increment(ref DtState.Runs);
            return default;
        }
    }

    public sealed class DtConcurrentA : IAsyncCommand<DtConcurrentSignal>
    {
        public async ValueTask ExecuteAsync(DtConcurrentSignal signal, CancellationToken ct)
        {
            Interlocked.Increment(ref DtState.ConcurrentStarted);
            await DtState.Gate.Task;
        }
    }

    public sealed class DtConcurrentB : IAsyncCommand<DtConcurrentSignal>
    {
        public async ValueTask ExecuteAsync(DtConcurrentSignal signal, CancellationToken ct)
        {
            Interlocked.Increment(ref DtState.ConcurrentStarted);
            await DtState.Gate.Task;
        }
    }

    public sealed class DtConcurrentDetached : IAsyncCommand<DtConcurrentSignal>
    {
        public ValueTask ExecuteAsync(DtConcurrentSignal signal, CancellationToken ct) => default;
    }

    public sealed class DtRuleSyncCommand : ICommand<DtRuleSignal>
    {
        public void Execute(DtRuleSignal signal) { }
    }

    public sealed class DtRuleAsyncCommand : IAsyncCommand<DtRuleSignal>
    {
        public ValueTask ExecuteAsync(DtRuleSignal signal, CancellationToken ct) => default;
    }

    public sealed class DtRuleOtherAsyncCommand : IAsyncCommand<DtRuleSignal>
    {
        public ValueTask ExecuteAsync(DtRuleSignal signal, CancellationToken ct) => default;
    }

    /// <summary>
    /// Detached commands (#155): <c>FireAsync</c> starts them first and does not await them, they run on the
    /// context's lifetime token, a failure goes through recovery, and a Detached handler shares a signal with the
    /// other modes but Exclusive without changing how those run.
    /// </summary>
    public static class DetachedCommandSuite
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine("[Detached] DETACHED COMMAND SUITE (#155)");
            Console.WriteLine("===============================================================================");

            failures += AssertPass("DT1. FireAsync_StartsADetachedCommandFirst_AndCompletesWhileItRuns", FireDoesNotWait);
            failures += AssertPass("DT2. AConcurrentPair_StillOverlaps_BesideADetachedCommand", ConcurrentStillOverlaps);
            failures += AssertPass("DT3. ADetachedFailure_GoesThroughRecovery_AndTheFireDoesNotFault", FailureGoesThroughRecovery);
            failures += AssertPass("DT4. AFireTimeout_LeavesADetachedCommandRunning_AndTheContextsDisposalCancelsIt", LifetimeToken);
            failures += AssertPass("DT5. ADetachedOneShot_RunsOnceAcrossTwoFires", OneShotRunsOnce);
            failures += AssertPass("DT6. Registration_RejectsASyncDetachedCommand_AndDetachedBesideExclusive_ButNotBesideSequential", RegistrationRules);
            failures += AssertPass("DT7. SyncFire_OnASignalWithADetachedCommand_ThrowsTheMismatch", SyncFireThrows);

            if (failures == 0)
                Console.WriteLine("\n[Detached] ALL DETACHED COMMAND TESTS PASSED ✓");
            else
                Console.WriteLine($"\n[Detached] {failures} DETACHED COMMAND TEST(S) FAILED ✗");
            return failures;
        }

        private static int AssertPass(string testName, Func<bool> testFunc)
        {
            try
            {
                bool passed = testFunc();
                Console.WriteLine($"[Detached] {(passed ? "PASS" : "FAIL")}  {testName}");
                return passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Detached] FAIL  {testName}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static bool Check(bool ok, string what)
        {
            if (!ok) Console.WriteLine("      " + what);
            return ok;
        }

        private static bool Within(Task task) => task.Wait(Bound);

        private static bool FireDoesNotWait()
        {
            DtState.Reset();
            using var ctx = NexusTestHarness.CreateContext(b =>
            {
                b.BindAsyncCommand<DtSignal, DtSequentialCommand>();
                b.BindAsyncCommand<DtSignal, DtGatedDetachedCommand>(ExecutionMode.Detached);
            });
            bool heard = false;
            ctx.Context.SignalBus.Subscribe<DtSignal>(_ => heard = true);

            var fire = ctx.Context.SignalBus.FireAsync(new DtSignal()).AsTask();
            bool ok = Check(Within(fire), "FireAsync waited for the detached command");
            ok &= Check(heard, "the subscriber did not hear the signal");
            ok &= Check(!DtState.Finished.Task.IsCompleted, "the detached command finished before its gate opened");
            string order;
            lock (DtState.Order) order = string.Join(",", DtState.Order);
            ok &= Check(order == "detached,sequential", $"start order {order}, expected detached,sequential");
            DtState.Gate.SetResult(true);
            ok &= Check(Within(DtState.Finished.Task), "the detached command did not finish after its gate opened");
            return ok;
        }

        private static bool ConcurrentStillOverlaps()
        {
            DtState.Reset();
            using var ctx = NexusTestHarness.CreateContext(b =>
            {
                b.BindAsyncCommand<DtConcurrentSignal, DtConcurrentA>(ExecutionMode.Concurrent);
                b.BindAsyncCommand<DtConcurrentSignal, DtConcurrentDetached>(ExecutionMode.Detached);
                b.BindAsyncCommand<DtConcurrentSignal, DtConcurrentB>(ExecutionMode.Concurrent);
            });
            var fire = ctx.Context.SignalBus.FireAsync(new DtConcurrentSignal()).AsTask();
            // Run one after the other, the second could not start while the first waits on the gate.
            var deadline = DateTime.UtcNow + Bound;
            while (Volatile.Read(ref DtState.ConcurrentStarted) < 2 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            bool ok = Check(DtState.ConcurrentStarted == 2, $"{DtState.ConcurrentStarted} of 2 concurrent commands started before the gate opened");
            DtState.Gate.SetResult(true);
            ok &= Check(Within(fire), "FireAsync did not complete after the gate opened");
            return ok;
        }

        private static bool FailureGoesThroughRecovery()
        {
            DtState.Reset();
            var seen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var strategy = new TestRecoveryStrategy
            {
                DecisionFactory = failure => { seen.TrySetResult(failure.Exception); return RecoveryDecision.Skip(); }
            };
            using var ctx = NexusTestHarness.CreateContext(b =>
            {
                b.BindInstance<IRecoveryStrategy>(strategy);
                b.BindAsyncCommand<DtSignal, DtThrowingDetachedCommand>(ExecutionMode.Detached);
            });
            var fire = ctx.Context.SignalBus.FireAsync(new DtSignal()).AsTask();
            bool ok = Check(Within(fire) && fire.Status == TaskStatus.RanToCompletion, $"the fire ended {fire.Status}");
            ok &= Check(Within(seen.Task), "the recovery strategy never saw the failure");
            ok &= Check(seen.Task.IsCompleted && seen.Task.Result is InvalidOperationException, $"the strategy saw {(seen.Task.IsCompleted ? seen.Task.Result?.GetType().Name : "nothing")}");
            return ok;
        }

        private static bool LifetimeToken()
        {
            DtState.Reset();
            var ctx = NexusTestHarness.CreateContext(b =>
            {
                b.BindAsyncCommand<DtSignal, DtHangingSequentialCommand>();
                b.BindAsyncCommand<DtSignal, DtWaitingDetachedCommand>(ExecutionMode.Detached);
            });
            bool ok;
            try
            {
                var fire = ctx.Context.SignalBus.FireAsyncWithTimeout(new DtSignal(), 50).AsTask();
                ok = Check(Within(fire.ContinueWith(_ => { })) && (fire.IsCanceled || fire.IsFaulted), $"the fire ended {fire.Status}, expected its timeout");
                ok &= Check(!DtState.Cancelled.Task.Wait(150), "the fire's timeout cancelled the detached command");
            }
            finally
            {
                ctx.Dispose();
            }
            ok &= Check(Within(DtState.Cancelled.Task), "the context's disposal did not cancel the detached command");
            return ok;
        }

        private static bool OneShotRunsOnce()
        {
            DtState.Reset();
            using var ctx = NexusTestHarness.CreateContext(b =>
                b.BindAsyncCommandOnce<DtSignal, DtCountingDetachedCommand>(ExecutionMode.Detached));
            bool ok = Check(Within(ctx.Context.SignalBus.FireAsync(new DtSignal()).AsTask()), "the first fire did not complete");
            ok &= Check(Within(ctx.Context.SignalBus.FireAsync(new DtSignal()).AsTask()), "the second fire did not complete");
            Thread.Sleep(20);
            ok &= Check(DtState.Runs == 1, $"the one-shot ran {DtState.Runs} times");
            return ok;
        }

        private static bool RegistrationRules()
        {
            bool Throws(Action register)
            {
                try { register(); return false; }
                catch (InvalidOperationException) { return true; }
            }

            using var sync = NexusTestHarness.CreateContext();
            bool ok = Check(Throws(() => sync.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleSyncCommand), ExecutionMode.Detached, 0, false)),
                "a sync ICommand registered as Detached");

            using var exclusiveFirst = NexusTestHarness.CreateContext();
            exclusiveFirst.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleAsyncCommand), ExecutionMode.Exclusive, 0, true);
            ok &= Check(Throws(() => exclusiveFirst.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleOtherAsyncCommand), ExecutionMode.Detached, 0, true)),
                "a Detached handler joined an Exclusive one");

            using var detachedFirst = NexusTestHarness.CreateContext();
            detachedFirst.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleOtherAsyncCommand), ExecutionMode.Detached, 0, true);
            ok &= Check(Throws(() => detachedFirst.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleAsyncCommand), ExecutionMode.Exclusive, 0, true)),
                "an Exclusive handler joined a Detached one");

            using var beside = NexusTestHarness.CreateContext();
            beside.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleAsyncCommand), ExecutionMode.Sequential, 0, true);
            beside.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleOtherAsyncCommand), ExecutionMode.Detached, 0, true);
            beside.Context.SignalBusInternal.RegisterCommand(typeof(DtRuleSignal), typeof(DtRuleSyncCommand), ExecutionMode.Sequential, 1, false);
            return ok;
        }

        private static bool SyncFireThrows()
        {
            DtState.Reset();
            using var ctx = NexusTestHarness.CreateContext(b =>
                b.BindAsyncCommand<DtSignal, DtCountingDetachedCommand>(ExecutionMode.Detached));
            try
            {
                ctx.Context.SignalBus.Fire(new DtSignal());
                return Check(false, "Fire did not throw");
            }
            catch (NexusSyncAsyncMismatchException)
            {
                return Check(DtState.Runs == 0, $"the detached command ran {DtState.Runs} times on the sync Fire");
            }
        }
    }
}
