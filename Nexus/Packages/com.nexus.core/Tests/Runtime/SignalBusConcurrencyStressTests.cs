using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;

namespace Nexus.Tests
{
    [TestFixture]
    public class SignalBusConcurrencyStressTests
    {
        public readonly struct TestSignal
        {
            public readonly int Id;
            public TestSignal(int id) { Id = id; }
        }

        public class ConcurrentAsyncCommand : IAsyncCommand<TestSignal>
        {
            private readonly Action _onExecute;
            public ConcurrentAsyncCommand(Action onExecute) => _onExecute = onExecute;
            public ValueTask ExecuteAsync(TestSignal signal, CancellationToken ct)
            {
                _onExecute?.Invoke();
                return default;
            }
        }

        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _container.BindInstance<ISignalBus>(_signalBus);
            _container.BindInstance(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            try { _signalBus?.Dispose(); } catch { }
            try { _poolManager?.Clear(); } catch { }
            try { _container?.Dispose(); } catch { }
        }

        [Test]
        public async Task SignalBus_AllowsManyConcurrentFireAsync_WithNoSilentFailures()
        {
            const int parallel = 200; // reasonable stress while staying lightweight in CI-less environment
            var executed = 0;
            var lockObj = new object();

            // Bind a factory that produces a command instance which increments executed atomically
            _container.BindFactory<ConcurrentAsyncCommand>((c) => new ConcurrentAsyncCommand(() => Interlocked.Increment(ref executed)));
            _signalBus.RegisterCommand(typeof(TestSignal), typeof(ConcurrentAsyncCommand), ExecutionMode.Concurrent, priority: 0, isAsync: true);

            var tasks = new List<Task>(parallel);
            for (int i = 0; i < parallel; i++)
            {
                int id = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await _signalBus.FireAsync(new TestSignal(id));
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail("FireAsync threw exception: " + ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.AreEqual(parallel, executed, "All concurrent commands should have executed exactly once each.");
        }
    }
}
