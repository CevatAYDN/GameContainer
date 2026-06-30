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

        public class ThrowCommand : ICommand
        {
            [Inject] private RecoveryTestResults _results;
            public FailSignal Signal;
            public void Execute()
            {
                _results.ThrowCount++;
                throw new InvalidOperationException("Command failed intendedly: " + Signal.Message);
            }
        }

        public class FallbackCommand : ICommand
        {
            [Inject] private RecoveryTestResults _results;
            public FailSignal Signal;
            public void Execute()
            {
                _results.FallbackCount++;
                _results.FallbackMessage = Signal.Message;
            }
        }

        public class AsyncFallbackCommand : IAsyncCommand
        {
            [Inject] private RecoveryTestResults _results;
            public FailSignal Signal;
            public ValueTask ExecuteAsync(CancellationToken ct)
            {
                _results.AsyncFallbackCount++;
                _results.AsyncFallbackMessage = Signal.Message;
                return default;
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
            var errorRegex = new System.Text.RegularExpressions.Regex("RetryTest");
            var warningRegex = new System.Text.RegularExpressions.Regex("Retry limit of 3 reached");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, warningRegex);

            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Retry(3);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.Fire(new FailSignal("RetryTest"));
            });

            Assert.IsTrue(ex.Message.Contains("Retry limit reached"));
            Assert.AreEqual(4, _results.ThrowCount); // 1 initial + 3 retries
        }

        [Test]
        public void Recovery_Fallback_ExecutesFallbackCommandAndPassesSignal()
        {
            var errorRegex = new System.Text.RegularExpressions.Regex("FallbackTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

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
            var errorRegex = new System.Text.RegularExpressions.Regex("FallbackAsyncTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.FallbackAsync<AsyncFallbackCommand>();

            await _signalBus.FireAsync(new FailSignal("FallbackAsyncTest"));

            Assert.AreEqual(1, _results.ThrowCount);
            Assert.AreEqual(1, _results.AsyncFallbackCount);
            Assert.AreEqual("FallbackAsyncTest", _results.AsyncFallbackMessage);
        }

        [Test]
        public void Recovery_Skip_SuppressesExceptionAndPublishesCommandFailedSignal()
        {
            var errorRegex = new System.Text.RegularExpressions.Regex("SkipTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

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
        public void Recovery_Abort_AbortsImmediatelyAndThrows()
        {
            var errorRegex = new System.Text.RegularExpressions.Regex("AbortTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Abort();

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _signalBus.Fire(new FailSignal("AbortTest"));
            });

            Assert.IsTrue(ex.Message.Contains("Execution aborted by recovery strategy"));
            Assert.AreEqual(1, _results.ThrowCount);
        }
    }
}
