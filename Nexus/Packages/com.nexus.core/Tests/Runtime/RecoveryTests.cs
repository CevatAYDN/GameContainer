using NUnit.Framework;
using Nexus.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Tests
{
    [TestFixture]
    public class RecoveryTests
    {
        /// <summary>Instance-based result collector to prevent static state pollution.</summary>
        public class RecoveryTestResults
        {
            public int ThrowCount;
            public int FallbackCount;
            public string FallbackMessage;
            public int AsyncFallbackCount;
            public string AsyncFallbackMessage;
        }

        public readonly struct FailSignal
        {
            public readonly string Message;
            public FailSignal(string message) => Message = message;
        }

        public class ThrowCommand : ICommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public void Execute(FailSignal signal)
            {
                _results.ThrowCount++;
                throw new InvalidOperationException("Command failed intendedly: " + signal.Message);
            }
        }

        public class FallbackCommand : ICommand, ICommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public FailSignal Signal;
            public void Execute() => Execute(Signal);
            public void Execute(FailSignal signal)
            {
                _results.FallbackCount++;
                _results.FallbackMessage = signal.Message;
            }
        }

        public class AsyncFallbackCommand : IAsyncCommand, IAsyncCommand<FailSignal>, ICommand, ICommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public FailSignal Signal;

            public void Execute() => Execute(Signal);
            public void Execute(FailSignal signal)
            {
                _results.AsyncFallbackCount++;
                _results.AsyncFallbackMessage = signal.Message;
            }

            public ValueTask ExecuteAsync(CancellationToken ct) => ExecuteAsync(Signal, ct);
            public ValueTask ExecuteAsync(FailSignal signal, CancellationToken ct)
            {
                Execute(signal);
                return default;
            }
        }

        // Generic-only commands (implement only ICommand<TSignal> / IAsyncCommand<TSignal>,
        // NOT the non-generic ICommand/IAsyncCommand). These exercise the object-based fallback
        // dispatch path, which used to silently no-op for them.
        public class GenericOnlyFallbackCommand : ICommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public void Execute(FailSignal signal)
            {
                _results.FallbackCount++;
                _results.FallbackMessage = signal.Message;
            }
        }

        public class GenericOnlyAsyncFallbackCommand : IAsyncCommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public ValueTask ExecuteAsync(FailSignal signal, CancellationToken ct)
            {
                _results.AsyncFallbackCount++;
                _results.AsyncFallbackMessage = signal.Message;
                return default;
            }
        }

        // Async command that always fails, so FireAsync routes through the async recovery
        // handler (HandleCommandErrorWithDecisionAsync) rather than the sync one.
        public class AsyncThrowCommand : IAsyncCommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public ValueTask ExecuteAsync(FailSignal signal, CancellationToken ct)
            {
                _results.ThrowCount++;
                throw new InvalidOperationException("Async command failed intendedly: " + signal.Message);
            }
        }

        // Hangs until the [CommandTimeout] linked token cancels it. If the timeout were not
        // wired into the retry loop (or the OCE were retried), FireAsync would either hang
        // forever or retry indefinitely — the test proves neither happens.
        [CommandTimeout(50)]
        public class HangingAsyncCommand : IAsyncCommand<FailSignal>
        {
            [Inject] private RecoveryTestResults _results;
            public async ValueTask ExecuteAsync(FailSignal signal, CancellationToken ct)
            {
                _results.ThrowCount++;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }

        public class CustomRecoveryStrategy : IRecoveryStrategy
        {
            public Func<CommandFailureContext, RecoveryDecision> DecisionFactory;

            public RecoveryDecision OnCommandFailed(CommandFailureContext failure)
            {
                return DecisionFactory != null ? DecisionFactory(failure) : RecoveryDecision.Skip();
            }
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;
        private CustomRecoveryStrategy _strategy;
        private RecoveryTestResults _results;

        [SetUp]
        public void Setup()
        {
            _results = new RecoveryTestResults();

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _strategy = new CustomRecoveryStrategy();

            _container.BindInstance<IRecoveryStrategy>(_strategy);
            _container.BindInstance(_results);
            _container.Bind<FallbackCommand>(isSingleton: false);
            _container.Bind<AsyncFallbackCommand>(isSingleton: false);
            _container.Bind<GenericOnlyFallbackCommand>(isSingleton: false);
            _container.Bind<GenericOnlyAsyncFallbackCommand>(isSingleton: false);
            _container.Bind<AsyncThrowCommand>(isSingleton: false);
            _container.Bind<HangingAsyncCommand>(isSingleton: false);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        // With the logger fallback the engine's "[Nexus] Command X failed" line reaches Unity's log once per failed attempt.
        private static void ExpectCommandFailed(string command, string message, int attempts = 1)
        {
            for (int i = 0; i < attempts; i++)
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] Command " + command + " failed: " + message));
        }

        [Test]
        public void Recovery_Retry_RetriesUpToLimitThenAborts()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Retry(3);

            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: RetryTest", attempts: 4); // 1 initial + 3 retries
            // the warnings this test causes, after the errors above (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\]\ Retry\ limit\ of\ \d+\ reached\.\ Forcing\ Abort\."));

            // The recovery engine surfaces the retry-limit abort as the TYPED
            // NexusRecoveryAbortException (an InvalidOperationException subclass).
            // Assert on the exact type: NUnit's Assert.Throws<T> requires an EXACT
            // type match, so Throws<InvalidOperationException> would reject the
            // specialized subclass even though it IS-A InvalidOperationException.
            var ex = Assert.Throws<NexusRecoveryAbortException>(() =>
            {
                _signalBus.Fire(new FailSignal("RetryTest"));
            });

            Assert.IsTrue(ex.Message.Contains("Retry limit reached"));
            Assert.AreEqual(4, _results.ThrowCount); // 1 initial + 3 retries
        }

        [Test]
        public void Recovery_Fallback_ExecutesFallbackCommandAndPassesSignal()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Fallback<FallbackCommand>();

            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: FallbackTest");
            _signalBus.Fire(new FailSignal("FallbackTest"));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(1, _results.FallbackCount);
            Assert.AreEqual("FallbackTest", _results.FallbackMessage);
        }

        [Test]
        public async Task Recovery_FallbackAsync_ExecutesAsyncFallbackCommandAndPassesSignal()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.FallbackAsync<AsyncFallbackCommand>();

            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: FallbackAsyncTest");
            await _signalBus.FireAsync(new FailSignal("FallbackAsyncTest"));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(1, _results.AsyncFallbackCount);
            Assert.AreEqual("FallbackAsyncTest", _results.AsyncFallbackMessage);
        }

        [Test]
        public void Recovery_Fallback_GenericOnlyCommand_ExecutesViaObjectDispatch()
        {
            // Regression: a fallback command implementing only ICommand<TSignal> (not ICommand)
            // used to be silently skipped by the object-based ExecuteCommand path.
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(GenericOnlyFallbackCommand), 0);

            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: GenericFallbackTest");
            _signalBus.Fire(new FailSignal("GenericFallbackTest"));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(1, _results.FallbackCount, "Generic-only fallback command must execute.");
            Assert.AreEqual("GenericFallbackTest", _results.FallbackMessage);
        }

        [Test]
        public async Task Recovery_FallbackAsync_GenericOnlyAsyncCommand_ExecutesViaObjectDispatch()
        {
            // Regression: a fallback command implementing only IAsyncCommand<TSignal> (not
            // IAsyncCommand) used to be silently skipped by the object-based async dispatch path.
            // An async throwing command ensures the async recovery handler is exercised.
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(AsyncThrowCommand), ExecutionMode.Sequential, 0, true);
            _strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(GenericOnlyAsyncFallbackCommand), 0);

            ExpectCommandFailed("AsyncThrowCommand", "Async command failed intendedly: GenericAsyncFallbackTest");
            await _signalBus.FireAsync(new FailSignal("GenericAsyncFallbackTest"));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(1, _results.AsyncFallbackCount, "Generic-only async fallback command must execute.");
            Assert.AreEqual("GenericAsyncFallbackTest", _results.AsyncFallbackMessage);
        }

        [Test]
        public void Recovery_Fallback_AsyncOnlyCommandInSyncContext_IsRejectedNotRecursed()
        {
            // Regression: an async-only fallback type returned from the SYNC error handler must
            // be rejected (treated as Skip) rather than dispatched — dispatching it would throw
            // and re-enter the recovery strategy with the same decision, recursing forever.
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => new RecoveryDecision(RecoveryAction.Fallback, typeof(GenericOnlyAsyncFallbackCommand), 0);

            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: SyncContextAsyncFallback");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, "[Nexus] Fallback command 'GenericOnlyAsyncFallbackCommand' cannot execute synchronously for signal 'FailSignal'. Treating as Skip.");
            Assert.DoesNotThrow(() => _signalBus.Fire(new FailSignal("SyncContextAsyncFallback")));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(0, _results.AsyncFallbackCount, "Async-only fallback must not run in a sync context.");
        }

        [Test]
        public void Recovery_Skip_SuppressesExceptionAndPublishesCommandFailedSignal()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Skip();

            CommandFailedSignal? caughtFailedSignal = null;
            _signalBus.Subscribe<CommandFailedSignal>(sig => caughtFailedSignal = sig);
            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: SkipTest");

            Assert.DoesNotThrow(() =>
            {
                _signalBus.Fire(new FailSignal("SkipTest"));
            });

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.IsNotNull(caughtFailedSignal);
            Assert.AreEqual(typeof(ThrowCommand), caughtFailedSignal.Value.SourceCommand);
            Assert.IsInstanceOf<FailSignal>(caughtFailedSignal.Value.SourceSignal);
            Assert.AreEqual("SkipTest", ((FailSignal)caughtFailedSignal.Value.SourceSignal).Message);
        }

        public enum FireMode { AsyncAndForget, NextFrame, ThreadSafe }

        private void FireIn(FireMode mode, FailSignal signal, Action<Exception> onError = null)
        {
            switch (mode)
            {
                case FireMode.AsyncAndForget: _signalBus.FireAsyncAndForget(signal, onError); break;
                case FireMode.NextFrame: _signalBus.FireNextFrame(signal); _container.Resolve<HybridQueue>().DrainNextFrame(); break;
                case FireMode.ThreadSafe: _signalBus.FireThreadSafe(signal); _container.Resolve<HybridQueue>().DrainThreadSafe(); break;
            }
        }

        private static async Task Until(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.IsTrue(done(), what + " within 5 s");
        }

        // Issue #168: a [CommandTimeout] expiry was an OperationCanceledException, which the recovery engine rethrew as if
        // the context were shutting down: no line, no CommandFailedSignal, no strategy. It is a failure the strategy sees
        // now, so a Retry strategy retries it, and the loop still ends at the strategy's limit.
        [Test]
        public async Task ACommandTimeout_IsAFailureTheStrategyRetries_AndTheRetryLoopEnds()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(HangingAsyncCommand), ExecutionMode.Sequential, 0, true);
            var timeouts = new System.Collections.Generic.List<bool>();
            _strategy.DecisionFactory = ctx => { timeouts.Add(ctx.IsTimeout); return RecoveryDecision.Retry(3); };
            ExpectCommandFailed("HangingAsyncCommand", @"timed out after 50 ms \(\[CommandTimeout\]\)", attempts: 4);
            // the warnings this test causes, after the errors above (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\]\ Retry\ limit\ of\ \d+\ reached\.\ Forcing\ Abort\."));

            // Raced against 5 s: a timeout that never fires would hang the signal line, and the test with it.
            var fireTask = _signalBus.FireAsync(new FailSignal("Timeout")).AsTask();
            var completed = await Task.WhenAny(fireTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(fireTask, completed, "FireAsync completes within 5 s: the [CommandTimeout] token fires and the retries end");

            Exception surfaced = null;
            try { await fireTask; }
            catch (Exception e) { surfaced = e; }
            Assert.IsInstanceOf<NexusRecoveryAbortException>(surfaced, "the retry limit aborts the chain");
            Assert.IsInstanceOf<TimeoutException>(surfaced.InnerException);
            Assert.AreEqual(4, _results.ThrowCount, "1 run + 3 retries");
            CollectionAssert.AreEqual(new[] { true, true, true, true }, timeouts, "the strategy is told every failure was a timeout");
        }

        // Issue #168: on the fire modes that do not await, a timeout reaches the strategy as a timeout, raises
        // CommandFailedSignal and is written once
        [TestCase(FireMode.AsyncAndForget)]
        [TestCase(FireMode.NextFrame)]
        [TestCase(FireMode.ThreadSafe)]
        public async Task ACommandTimeout_ReachesTheStrategyAndTheFailedSignal_WhateverTheFireMode(FireMode mode)
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(HangingAsyncCommand), ExecutionMode.Sequential, 0, true);
            bool? isTimeout = null;
            _strategy.DecisionFactory = ctx => { isTimeout = ctx.IsTimeout; return RecoveryDecision.Skip(); };
            CommandFailedSignal? failed = null;
            _signalBus.Subscribe<CommandFailedSignal>(s => failed = s);
            ExpectCommandFailed("HangingAsyncCommand", "timed out after 50 ms");

            FireIn(mode, new FailSignal("Timeout"));
            await Until(() => failed != null, "CommandFailedSignal");
            Assert.AreEqual(true, isTimeout);
            Assert.AreEqual(typeof(HangingAsyncCommand), failed.Value.SourceCommand);
        }

        // Issue #168: an aborted failure on the queued and fire-and-forget paths was written twice, by the recovery engine
        // and again by the queue drain or the fire-and-forget runner. onError still receives it.
        [TestCase(FireMode.AsyncAndForget, true)]
        [TestCase(FireMode.NextFrame, false)]
        [TestCase(FireMode.ThreadSafe, true)]
        public async Task AnAbortedFailure_IsOneConsoleLine_OnTheQueuedAndFireAndForgetPaths(FireMode mode, bool asyncCommand)
        {
            var command = asyncCommand ? typeof(AsyncThrowCommand) : typeof(ThrowCommand);
            _signalBus.RegisterCommand(typeof(FailSignal), command, ExecutionMode.Sequential, 0, asyncCommand);
            int decisions = 0;
            _strategy.DecisionFactory = ctx => { decisions++; return RecoveryDecision.Abort(); };
            Exception reported = null;
            ExpectCommandFailed(command.Name, "");

            FireIn(mode, new FailSignal("Abort"), e => reported = e);
            await Until(() => decisions == 1, "the strategy decided");
            await Task.Delay(100);   // a second line, if any, comes after the decision
            if (mode == FireMode.AsyncAndForget)
                Assert.IsInstanceOf<NexusRecoveryAbortException>(reported, "onError receives the abort");
        }

        // Issue #168: a context shutting down still cancels its commands quietly; only a command's own timeout is a failure
        [Test]
        public async Task ACommandCancelledByShutdown_IsNoFailure_AndWritesNothing()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(HangingAsyncCommand), ExecutionMode.Sequential, 0, true);
            int decisions = 0;
            _strategy.DecisionFactory = ctx => { decisions++; return RecoveryDecision.Skip(); };
            _signalBus.FireAsyncAndForget(new FailSignal("Shutdown"));
            await Until(() => _results.ThrowCount == 1, "the command started");

            _signalBus.Dispose();
            await Task.Delay(200);   // past the command's 50 ms timeout
            Assert.AreEqual(0, decisions, "a shutdown is not a failure");
        }

        [Test]
        public void Recovery_Abort_AbortsImmediatelyAndThrows()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Abort();

            // Same exact-type rationale as the Retry test: the Abort decision throws the
            // typed NexusRecoveryAbortException (an InvalidOperationException subclass).
            ExpectCommandFailed("ThrowCommand", "Command failed intendedly: AbortTest");
            var ex = Assert.Throws<NexusRecoveryAbortException>(() =>
            {
                _signalBus.Fire(new FailSignal("AbortTest"));
            });

            Assert.IsTrue(ex.Message.Contains("Execution aborted by recovery strategy"));
            Assert.AreEqual(1, _results.ThrowCount);
        }

        [Test]
        public async Task CreatePureContextAsync_RegistersContextAndUsesScopeTag()
        {
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the pure context has no lifecycle
            var context = await NexusRuntime.CreatePureContextAsync("ReusableScope", new[] { "Assembly-CSharp" });

            try
            {
                Assert.IsNotNull(context, "Pure context creation should return a valid context.");
                Assert.AreEqual("ReusableScope", context.ScopeTag);
                Assert.IsNotNull(NexusRuntime.GetContext("ReusableScope"));
                Assert.AreEqual(context, NexusRuntime.GetContext("ReusableScope"));
                Assert.IsTrue(NexusRuntime.ActiveContexts.Count >= 1, "Pure context should be visible in the active registry.");
            }
            finally
            {
                context?.Dispose();
                NexusRuntime.Reset();
            }
        }

        [Test]
        public void NexusTestContext_Dispose_ClearsWrappedContextAndSubscriptions()
        {
            var testContext = NexusTestHarness.CreateContext("HarnessScope");

            try
            {
                Assert.IsNotNull(testContext);
                Assert.IsNotNull(testContext.Context);
                Assert.AreEqual("HarnessScope", testContext.Context.ScopeTag);
            }
            finally
            {
                testContext.Dispose();
                Assert.IsNull(NexusRuntime.GetContext("HarnessScope"));
                NexusRuntime.Reset();
            }
        }

        [Test]
        public async Task CreatePureContextAsync_DisposesCleanlyAndRefreshesRegistry()
        {
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered")); // the pure context has no lifecycle
            var context = await NexusRuntime.CreatePureContextAsync("RegistryRefreshScope", new[] { "Assembly-CSharp" });

            try
            {
                Assert.AreEqual(context, NexusRuntime.GetContext("RegistryRefreshScope"));
                Assert.That(NexusRuntime.ActiveContexts, Has.Member(context));
            }
            finally
            {
                context.Dispose();
                NexusRuntime.Reset();
            }

            Assert.IsNull(NexusRuntime.GetContext("RegistryRefreshScope"));
            Assert.That(NexusRuntime.ActiveContexts, Has.No.Member(context));
        }

        [Test]
        public void ContextData_DefaultValues_AreReusableFriendly()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();

            try
            {
                Assert.IsTrue(data.EnableAutoDiscovery, "Auto-discovery should remain enabled by default for quick project setup.");
                Assert.AreEqual(4, data.CommandPoolInitialSize, "Default command pool size should stay small and predictable.");
                Assert.AreEqual(64, data.CommandPoolMaxSize, "Default max command pool size should remain bounded.");
                Assert.AreEqual(2000, data.TracerRingBufferSize, "Default tracer buffer should remain production-friendly.");
                Assert.IsTrue(string.IsNullOrEmpty(data.ScopeTag), "ScopeTag should stay opt-in so projects can define their own naming.");
            }
            finally
            {
                if (data != null)
                {
                    UnityEngine.Object.DestroyImmediate(data);
                }
            }
        }
    }
}
