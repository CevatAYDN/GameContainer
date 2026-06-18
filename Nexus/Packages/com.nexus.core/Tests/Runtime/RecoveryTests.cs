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
        public readonly struct FailSignal
        {
            public readonly string Message;
            public FailSignal(string message) => Message = message;
        }

        public class ThrowCommand : ICommand
        {
            public static int ExecutionCount;
            public FailSignal Signal;
            public void Execute()
            {
                ExecutionCount++;
                throw new InvalidOperationException("Command failed intendedly: " + Signal.Message);
            }
        }

        public class FallbackCommand : ICommand
        {
            public static int ExecutionCount;
            public static string ReceivedMessage;
            public FailSignal Signal;
            public void Execute()
            {
                ExecutionCount++;
                ReceivedMessage = Signal.Message;
            }
        }

        public class AsyncFallbackCommand : IAsyncCommand
        {
            public static int ExecutionCount;
            public static string ReceivedMessage;
            public FailSignal Signal;
            public ValueTask ExecuteAsync(CancellationToken ct)
            {
                ExecutionCount++;
                ReceivedMessage = Signal.Message;
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

        [SetUp]
        public void Setup()
        {
            ThrowCommand.ExecutionCount = 0;
            FallbackCommand.ExecutionCount = 0;
            FallbackCommand.ReceivedMessage = null;
            AsyncFallbackCommand.ExecutionCount = 0;
            AsyncFallbackCommand.ReceivedMessage = null;

            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _strategy = new CustomRecoveryStrategy();

            _container.BindInstance<IRecoveryStrategy>(_strategy);
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
            Assert.AreEqual(4, ThrowCommand.ExecutionCount); // 1 initial + 3 retries
        }

        [Test]
        public void Recovery_Fallback_ExecutesFallbackCommandAndPassesSignal()
        {
            var errorRegex = new System.Text.RegularExpressions.Regex("FallbackTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Fallback<FallbackCommand>();

            _signalBus.Fire(new FailSignal("FallbackTest"));

            Assert.AreEqual(1, ThrowCommand.ExecutionCount);
            Assert.AreEqual(1, FallbackCommand.ExecutionCount);
            Assert.AreEqual("FallbackTest", FallbackCommand.ReceivedMessage);
        }

        [Test]
        public async Task Recovery_FallbackAsync_ExecutesAsyncFallbackCommandAndPassesSignal()
        {
            var errorRegex = new System.Text.RegularExpressions.Regex("FallbackAsyncTest");
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, errorRegex);

            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.FallbackAsync<AsyncFallbackCommand>();

            // Triggering synchronously will delegate to async path internally since FallbackAsync is async
            await _signalBus.FireAsync(new FailSignal("FallbackAsyncTest"));

            Assert.AreEqual(1, ThrowCommand.ExecutionCount);
            Assert.AreEqual(1, AsyncFallbackCommand.ExecutionCount);
            Assert.AreEqual("FallbackAsyncTest", AsyncFallbackCommand.ReceivedMessage);
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

            // Skip decision should catch the exception and NOT throw it to the caller
            Assert.DoesNotThrow(() =>
            {
                _signalBus.Fire(new FailSignal("SkipTest"));
            });

            Assert.AreEqual(1, ThrowCommand.ExecutionCount);
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
            Assert.AreEqual(1, ThrowCommand.ExecutionCount); // no retries
        }
    }
}
