// Runtime regression for the RESTORED sync error-handler tail in SignalBus.cs.
// Background: commit 5cec7ec had a swallowed tail in
// HandleCommandErrorWithDecision (missing Retry block, try/catch, fallthrough,
// closing brace) that made the whole package non-compiling. It was restored from
// git history, but the recovery behavior itself has only been compile-verified
// (RecoveryTests can't run without Unity). This harness scenario exercises the
// restored code paths at runtime: sync fallback dispatch, retry counting,
// async-only-fallback rejection (no infinite recursion), and [CommandTimeout]
// cancellation of a hanging command (REC1/REC2 — the dispose-before-await bug
// that froze Unity PlayMode in the EditMode/Runtime RecoveryTests).

using System;
using Nexus.Core;

namespace NexusBench
{
    public readonly struct FailSignal
    {
        public readonly int Id;
        public FailSignal(int id) => Id = id;
    }

    public class FailCounter { public int Value; }

    public class ThrowingCommand : ICommand<FailSignal>
    {
        [Inject] public FailCounter Counter;
        public void Execute(FailSignal s) => throw new InvalidOperationException("boom");
    }

    /// <summary>Generic-only command (ICommand&lt;TSignal&gt;, not ICommand) — exercises GetGenericSyncDispatcher.</summary>
    public class GenericOnlyFallbackCommand : ICommand<FailSignal>
    {
        [Inject] public FailCounter Counter;
        public void Execute(FailSignal s) => Counter.Value++;
    }

    /// <summary>Async-only command — must be REJECTED by the sync error handler (no recursion).</summary>
    public class AsyncOnlyFallbackCommand : IAsyncCommand<FailSignal>
    {
        public System.Threading.Tasks.ValueTask ExecuteAsync(FailSignal signal, System.Threading.CancellationToken ct)
        {
            return default;
        }
    }

    /// <summary>Throws on the first call, succeeds afterwards — for retry counting.</summary>
    public class RetryOnceCommand : ICommand<FailSignal>
    {
        [Inject] public FailCounter Counter;
        public void Execute(FailSignal s)
        {
            if (Counter.Value == 0)
            {
                Counter.Value++;
                throw new InvalidOperationException("retry me");
            }
            Counter.Value++;
        }
    }

    /// <summary>
    /// Hangs until the [CommandTimeout] linked token cancels it. If the timeout CTS were
    /// disposed before the command awaited (the bug this guards), the CancelAfter timer
    /// would never fire and this command would block the signal line forever — which froze
    /// Unity PlayMode in RecoveryTests.CommandTimeout_CancelsHangingCommand_DoesNotBlockRetryLoop.
    /// </summary>
    [CommandTimeout(50)]
    public class HangingCommand : IAsyncCommand<FailSignal>
    {
        [Inject] public FailCounter Counter;
        public async System.Threading.Tasks.ValueTask ExecuteAsync(FailSignal signal, System.Threading.CancellationToken ct)
        {
            Counter.Value++;
            await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        }
    }

    public sealed class TestRecoveryStrategy : IRecoveryStrategy
    {
        public Func<CommandFailureContext, RecoveryDecision> DecisionFactory = ctx => RecoveryDecision.Skip();
        public RecoveryDecision OnCommandFailed(CommandFailureContext failure) => DecisionFactory(failure);
    }

    public static class RecoveryRegression
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("[Nexus Benchmark] === Recovery regression (restored sync handler tail) ===");
            _failures = 0;

            TestSyncFallbackGenericOnlyDispatch();
            TestRetryCounting();
            TestAsyncOnlyFallbackRejectedInSyncContext();
            TestNoStrategyRegistered_FallsThroughToSkip();
            TestCommandTimeoutCancelsHangingCommand();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Nexus Benchmark] RECOVERY REGRESSION PASSED ✓"
                : $"[Nexus Benchmark] {_failures} RECOVERY REGRESSION(S) FAILED ✗");
            return _failures;
        }

        // Regression for the dispose-before-await timeout bug: the timeout CTS used to be
        // scoped to the `if` block, so it was disposed before the command awaited — the
        // CancelAfter timer never fired and a hanging command blocked the signal line
        // forever (Unity PlayMode froze). The timeout must cancel the command (OCE) WITHOUT
        // entering the retry loop (BuildPlan rethrows OCE before the strategy). The bounded
        // race keeps this check a fast failure instead of a hang if the bug regresses.
        private static void TestCommandTimeoutCancelsHangingCommand()
        {
            var strategy = new TestRecoveryStrategy { DecisionFactory = ctx => RecoveryDecision.Retry(10) };
            var (container, bus, pool, counter, _) = Setup(strategy);
            try
            {
                bus.RegisterCommand(typeof(FailSignal), typeof(HangingCommand), ExecutionMode.Sequential, 0, true);

                var fireTask = bus.FireAsync(new FailSignal(7)).AsTask();
                var completed = System.Threading.Tasks.Task.WhenAny(
                    fireTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();

                bool completedPromptly = ReferenceEquals(fireTask, completed);
                bool threwOce = false;
                if (completedPromptly)
                {
                    try { fireTask.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { threwOce = true; }
                }

                Check("REC1. CommandTimeout_CancelsHangingCommand_WithinBound",
                    completedPromptly && threwOce,
                    $"completed={completedPromptly} oce={threwOce} executions={counter.Value}");
                Check("REC2. CommandTimeout_DoesNotEnterRetryLoop",
                    counter.Value == 1,
                    $"executions={counter.Value} (must be 1 — OCE rethrows instead of retrying)");
            }
            finally
            {
                Teardown(bus, pool, container);
            }
        }

        private static (NexusDI, SignalBus, CommandPoolManager, FailCounter, TestRecoveryStrategy) Setup(TestRecoveryStrategy strategy)
        {
            var counter = new FailCounter();
            var container = new NexusDI();
            container.BindInstance(counter);
            container.Bind<ThrowingCommand>(isSingleton: false);
            container.Bind<GenericOnlyFallbackCommand>(isSingleton: false);
            container.Bind<RetryOnceCommand>(isSingleton: false);
            container.Bind<AsyncOnlyFallbackCommand>(isSingleton: false);
            container.Bind<HangingCommand>(isSingleton: false);
            if (strategy != null)
            {
                container.BindInstance<IRecoveryStrategy>(strategy);
            }
            var poolManager = new CommandPoolManager(container);
            var bus = new SignalBus(container, poolManager, new MockContext());
            return (container, bus, poolManager, counter, strategy);
        }

        private static void Teardown(SignalBus bus, CommandPoolManager pool, NexusDI container)
        {
            bus.Dispose();
            pool.Clear();
            container.Dispose();
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Nexus Benchmark] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("RecoveryRegression", name, ok, detail);
            if (!ok) _failures++;
        }

        private static void TestSyncFallbackGenericOnlyDispatch()
        {
            var strategy = new TestRecoveryStrategy
            {
                // Generic-only command (ICommand<TSignal>, not ICommand): the generic
                // Fallback<T>() helper is constrained to ICommand, so use the ctor form
                // (same pattern as RecoveryTests.cs around line 200).
                DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(GenericOnlyFallbackCommand), 0)
            };
            var (container, bus, pool, counter, _) = Setup(strategy);
            try
            {
                bus.RegisterCommand(typeof(FailSignal), typeof(ThrowingCommand), ExecutionMode.Sequential, 0, false);
                bool threw = false;
                try
                {
                    bus.Fire(new FailSignal(1));
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine($"[Nexus Benchmark] unexpected exception: {ex.GetType().Name}: {ex.Message}");
                }

                // The restored tail dispatches the fallback; counter.Value becomes 1.
                Check("SyncFallback_GenericOnlyDispatch", !threw && counter.Value == 1,
                    $"threw={threw}, fallbackRuns={counter.Value} (expected 1)");
            }
            finally
            {
                Teardown(bus, pool, container);
            }
        }

        private static void TestRetryCounting()
        {
            var strategy = new TestRecoveryStrategy
            {
                // Retry with max 3 — RetryOnceCommand throws once then succeeds.
                DecisionFactory = ctx => RecoveryDecision.Retry(3)
            };
            var (container, bus, pool, counter, _) = Setup(strategy);
            try
            {
                bus.RegisterCommand(typeof(FailSignal), typeof(RetryOnceCommand), ExecutionMode.Sequential, 0, false);
                bus.Fire(new FailSignal(1));

                // Attempt 1 throws (Counter 0→1), retry, attempt 2 succeeds (Counter 1→2).
                Check("Retry_Counting", counter.Value == 2,
                    $"calls={counter.Value} (expected 2: one throw + one success)");
            }
            finally
            {
                Teardown(bus, pool, container);
            }
        }

        private static void TestAsyncOnlyFallbackRejectedInSyncContext()
        {
            var strategy = new TestRecoveryStrategy
            {
                // Fallback targets an async-only command from a SYNC context.
                DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(AsyncOnlyFallbackCommand), 0)
            };
            var (container, bus, pool, counter, _) = Setup(strategy);
            try
            {
                bus.RegisterCommand(typeof(FailSignal), typeof(ThrowingCommand), ExecutionMode.Sequential, 0, false);
                bool threw = false;
                try
                {
                    bus.Fire(new FailSignal(1));
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine($"[Nexus Benchmark] unexpected exception: {ex.GetType().Name}: {ex.Message}");
                }

                // IsSyncCapableFallbackType rejects the async-only type → logged, treated as
                // Skip → no infinite recursion, no exception. Counter stays 0.
                Check("AsyncOnlyFallback_RejectedNotRecursed", !threw && counter.Value == 0,
                    $"threw={threw}, fallbackRuns={counter.Value} (expected 0)");
            }
            finally
            {
                Teardown(bus, pool, container);
            }
        }

        private static void TestNoStrategyRegistered_FallsThroughToSkip()
        {
            // No IRecoveryStrategy bound → the handler falls through to
            // FireFailedSignalSafe(failedSignal) + return RecoveryAction.Skip.
            var (container, bus, pool, counter, _) = Setup(null);
            try
            {
                bus.RegisterCommand(typeof(FailSignal), typeof(ThrowingCommand), ExecutionMode.Sequential, 0, false);
                bool threw = false;
                try
                {
                    bus.Fire(new FailSignal(1));
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine($"[Nexus Benchmark] unexpected exception: {ex.GetType().Name}: {ex.Message}");
                }

                Check("NoStrategy_FallsThroughToSkip", !threw,
                    $"threw={threw} (expected false — error contained, dispatch continues)");
            }
            finally
            {
                Teardown(bus, pool, container);
            }
        }
    }
}
