// Runtime regression for the RESTORED sync error-handler tail in SignalBus.cs.
// Background: commit 5cec7ec had a swallowed tail in
// HandleCommandErrorWithDecision (missing Retry block, try/catch, fallthrough,
// closing brace) that made the whole package non-compiling. It was restored from
// git history, but the recovery behavior itself has only been compile-verified
// (RecoveryTests can't run without Unity). This harness scenario exercises the
// restored code paths at runtime: sync fallback dispatch, retry counting, and
// async-only-fallback rejection (no infinite recursion).

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

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Nexus Benchmark] RECOVERY REGRESSION PASSED ✓"
                : $"[Nexus Benchmark] {_failures} RECOVERY REGRESSION(S) FAILED ✗");
            return _failures;
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
