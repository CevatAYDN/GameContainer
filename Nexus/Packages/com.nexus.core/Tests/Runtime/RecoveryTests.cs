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
            UnityEngine.Debug.Log($"[DIAG] START {NUnit.Framework.TestContext.CurrentContext.Test.FullName}");
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

        [Test]
        public void Recovery_Retry_RetriesUpToLimitThenAborts()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Retry(3);

            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, "[Nexus Error] Retry limit reached for command ThrowCommand.");

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

        [Test]
        public async Task CommandTimeout_CancelsHangingCommand_DoesNotBlockRetryLoop()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(HangingAsyncCommand), ExecutionMode.Sequential, 0, true);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Retry(10); // would loop forever if timeout didn't break out

            // Race FireAsync against a bounded delay: if the timeout mechanism regresses and
            // never fires, the task never completes — a plain await (or a blocking ThrowsAsync)
            // would hang CI forever. The race turns that regression into a fast, clearly-
            // messaged failure instead.
            var fireTask = _signalBus.FireAsync(new FailSignal("Timeout")).AsTask();
            var completed = await Task.WhenAny(fireTask, Task.Delay(TimeSpan.FromSeconds(5)));

            // fireTask winning the 5s race already proves prompt completion (the 50ms
            // timeout fired well inside the bound); no separate elapsed assertion needed.
            Assert.AreSame(fireTask, completed,
                "FireAsync must complete within 5s — if the [CommandTimeout] linked token stops firing, a hanging command blocks the signal line forever.");

            // fireTask is guaranteed complete here (proven by the AreSame race above), so
            // re-awaiting it cannot deadlock or hang. Surface its exception manually instead of
            // using Assert.ThrowsAsync: that API's return type differs across NUnit versions
            // (some return Task<T>; Unity's ext.nunit returns the exception directly and is
            // NOT awaitable — CS1061), so the try/catch form is version-agnostic. The command
            // must surface an OCE (rethrow, P1-3), NOT a retried generic failure (which would
            // loop Retry(10) and blow ThrowCount).
            bool threwOperationCanceled = false;
            try
            {
                await fireTask;
            }
            catch (OperationCanceledException)
            {
                threwOperationCanceled = true;
            }
            Assert.IsTrue(threwOperationCanceled,
                "A [CommandTimeout] async command must surface as OperationCanceledException.");
            Assert.AreEqual(1, _results.ThrowCount,
                "A timed-out command must NOT be retried: OperationCanceledException rethrows (P1-3) instead of entering the retry loop.");
        }

        [Test]
        public void Recovery_Abort_AbortsImmediatelyAndThrows()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Abort();

            // Same exact-type rationale as the Retry test: the Abort decision throws the
            // typed NexusRecoveryAbortException (an InvalidOperationException subclass).
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
