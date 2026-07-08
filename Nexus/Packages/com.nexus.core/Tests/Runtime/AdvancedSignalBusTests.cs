using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class AdvancedSignalBusTests
    {
        /// <summary>Instance-based result collector to prevent static state pollution.</summary>
        public class TestResults
        {
            public int ExecutionCount;
            public string LastMessage;
            public bool InterceptorBlocked;
        }

        public readonly struct TestSignal
        {
            public readonly int Id;
            public readonly string Message;
            public TestSignal(int id, string message = "") { Id = id; Message = message; }
        }

        public readonly struct AnotherSignal { public readonly int Value; public AnotherSignal(int v) => Value = v; }

        public class SimpleCommand : ICommand<TestSignal>
        {
            [Inject] private TestResults _results;
            public void Execute(TestSignal signal) { _results.ExecutionCount++; _results.LastMessage = signal.Message; }
        }

        public class FailCommand : ICommand<TestSignal>
        {
            [Inject] private TestResults _results;
            public void Execute(TestSignal signal)
            {
                _results.ExecutionCount++;
                throw new InvalidOperationException("Intentional failure: " + signal.Message);
            }
        }

        public class AsyncCommand : IAsyncCommand<TestSignal>
        {
            [Inject] private TestResults _results;
            public async ValueTask ExecuteAsync(TestSignal signal, CancellationToken ct)
            {
                await Task.Delay(1, ct);
                _results.ExecutionCount++;
                _results.LastMessage = signal.Message;
            }
        }

        /// <summary>A signal interceptor that can block or modify signals for testing.</summary>
        public class TestInterceptor : ISignalInterceptor
        {
            public Func<object, bool> OnIntercept;
            public bool Intercept(ref object signal)
            {
                _lastSignal = signal;
                return OnIntercept?.Invoke(signal) ?? true;
            }
            public object _lastSignal;
        }

        /// <summary>Plugin that registers a signal interceptor for testing.</summary>
        public class TestInterceptorPlugin : INexusPlugin
        {
            private readonly TestInterceptor _interceptor;
            public NexusPluginManifest Manifest { get; }
            public TestInterceptorPlugin(TestInterceptor interceptor)
            {
                _interceptor = interceptor;
                Manifest = new NexusPluginManifest("TestInterceptor", "1.0", PluginCapabilities.SignalInterceptor);
            }
            public void OnPluginRegistered(IPluginContext context) => context.RegisterSignalInterceptor(_interceptor);
            public void OnPluginRemoved() { }
        }

        public class WriteBlockCommand : ICommand<AnotherSignal>
        {
            [Inject] private TestResults _results;
            public void Execute(AnotherSignal signal) { _results.ExecutionCount++; }
        }

        public class FailAsyncCommand : IAsyncCommand<TestSignal>
        {
            [Inject] private TestResults _results;
            public async ValueTask ExecuteAsync(TestSignal signal, CancellationToken ct)
            {
                await Task.Delay(1, ct);
                _results.ExecutionCount++;
                throw new InvalidOperationException("Async failure: " + signal.Message);
            }
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;
        private TestResults _results;

        [SetUp]
        public void Setup()
        {
            _results = new TestResults();
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
            _container.BindInstance(_results);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus?.Dispose();
            _poolManager?.Clear();
            _container?.Dispose();
        }

        // ── DefaultRecoveryStrategy tests ──────────────────────

        [Test]
        public void DefaultRecoveryStrategy_WithFireAsync_ThrowsOnExhaustion()
        {
            var strategy = new DefaultRecoveryStrategy(maxRetries: 1);
            _container.BindInstance<IRecoveryStrategy>(strategy);
            _container.Bind<FailCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TestSignal), typeof(FailCommand), ExecutionMode.Sequential, 0, isAsync: false);

            Assert.Throws<InvalidOperationException>(() =>
                _signalBus.Fire(new TestSignal(5, "async-retry"))
            );
            Assert.AreEqual(2, _results.ExecutionCount, "1 initial + 1 retry = 2");
        }

        [Test]
        public async Task DefaultRecoveryStrategy_WithFireAsync_FallbackAsync_Skips()
        {
            var strategy = new DefaultRecoveryStrategy(maxRetries: 2);
            _container.BindInstance<IRecoveryStrategy>(strategy);
            _container.Bind<FailAsyncCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TestSignal), typeof(FailAsyncCommand), ExecutionMode.Sequential, 0, isAsync: true);

            try
            {
                await _signalBus.FireAsync(new TestSignal(6, "async-fail"));
                Assert.Fail("Should have thrown");
            }
            catch (InvalidOperationException ex)
            {
                Assert.IsTrue(ex.Message.Contains("Retry limit reached") || ex.Message.Contains("async") || ex.Message.Contains("aborted"), "Exception should indicate failure: " + ex.Message);
            }
            // Retries are handled by the recovery pipeline; the async path should retry
            // then abort when exhausted.
        }
    }
}
