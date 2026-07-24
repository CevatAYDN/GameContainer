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
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Retry(3);

            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, "[Nexus Error] Retry limit reached for command ThrowCommand.");

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
        public void Recovery_Abort_AbortsImmediatelyAndThrows()
        {
            _signalBus.RegisterCommand(typeof(FailSignal), typeof(ThrowCommand), ExecutionMode.Sequential, 0, false);
            _strategy.DecisionFactory = ctx => RecoveryDecision.Abort();

            var ex = Assert.Throws<InvalidOperationException>(() =>
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
        public void ContextData_DefaultValues_AreReusableFriendly()
        {
            var data = UnityEngine.ScriptableObject.CreateInstance<ContextData>();

            try
            {
                Assert.IsTrue(data.EnableAutoDiscovery, "Auto-discovery should remain enabled by default for quick project setup.");
                Assert.AreEqual(4, data.CommandPoolInitialSize, "Default command pool size should stay small and predictable.");
                Assert.AreEqual(64, data.CommandPoolMaxSize, "Default max command pool size should remain bounded.");
                Assert.AreEqual(2000, data.TracerRingBufferSize, "Default tracer buffer should remain production-friendly.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [Test]
        public async Task CreatePureContextAsync_DisposesCleanlyAndRefreshesRegistry()
        {
            var context = await NexusRuntime.CreatePureContextAsync("RegistryRefreshScope", new[] { "Assembly-CSharp" });

            try
            {
                Assert.AreEqual(context, NexusRuntime.GetContext("RegistryRefreshScope"));
                Assert.That(NexusRuntime.ActiveContexts, Does.Contain(context));
            }
            finally
            {
                context.Dispose();
                NexusRuntime.Reset();
            }

            Assert.IsNull(NexusRuntime.GetContext("RegistryRefreshScope"));
            Assert.That(NexusRuntime.ActiveContexts, Does.Not.Contain(context));
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
