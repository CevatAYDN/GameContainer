using System.Threading;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Play Mode benchmark lock for the <see cref="NexusProfiler"/> counters. Unlike the
    /// Edit Mode smoke tests (which assert only "counter moved"), these assert the EXACT
    /// delta: N synchronous fires/resolves must grow each counter by exactly N — a dropped
    /// instrumentation site would under-count, a double increment would over-count.
    ///
    /// Exactness relies on reading before/after inside a single synchronous test body: the
    /// profiler's FlushOnEndOfFrame only runs at frame boundaries, which cannot occur while
    /// the main thread is inside a plain [Test] (no yields).
    /// </summary>
    [TestFixture]
    public class NexusProfilerPlayModeTests
    {
        private const int N = 1000;

        private struct BenchSignal { public int Value; }
        private struct BenchCompositeA { }
        private struct BenchCompositeB { }

        private class BenchCommand : ICommand<BenchSignal>
        {
            public static int Executions;
            public void Execute(BenchSignal signal) => Executions++;
        }

        private class BenchCompositeCommand : ICompositeCommand
        {
            public static int Executions;
            public void Execute(CompositeContext signals) => Executions++;
        }

        private class BenchResolvable { }

        [SetUp]
        public void SetUp()
        {
            BenchCommand.Executions = 0;
            BenchCompositeCommand.Executions = 0;
            NexusRuntime.Reset();
        }

        [TearDown]
        public void TearDown() => NexusRuntime.Reset();

        private static SignalBus CreateBus(NexusDI di) => new(di, new CommandPoolManager(di), new MockContext());

        [Test]
        public void Fire_NTimes_IncrementsSignalsDispatchedByExactlyN()
        {
            using var di = new NexusDI();
            var bus = CreateBus(di);
            try
            {
                bus.RegisterCommand(typeof(BenchSignal), typeof(BenchCommand),
                    ExecutionMode.Sequential, priority: 0, isAsync: false);

                int before = NexusProfiler.SignalsDispatched.Value;
                for (int i = 0; i < N; i++) bus.Fire(new BenchSignal { Value = i });
                int after = NexusProfiler.SignalsDispatched.Value;

                Assert.AreEqual(N, after - before,
                    "SignalsDispatched must grow by exactly N for N synchronous fires (no drops, no double counts).");
                Assert.AreEqual(N, BenchCommand.Executions, "Every fire must have dispatched its command.");
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void CommandDispatch_NFires_IncrementsCommandsExecutedByExactlyN()
        {
            using var di = new NexusDI();
            var bus = CreateBus(di);
            try
            {
                bus.RegisterCommand(typeof(BenchSignal), typeof(BenchCommand),
                    ExecutionMode.Sequential, priority: 0, isAsync: false);

                int before = NexusProfiler.CommandsExecuted.Value;
                for (int i = 0; i < N; i++) bus.Fire(new BenchSignal { Value = i });
                int after = NexusProfiler.CommandsExecuted.Value;

                Assert.AreEqual(N, after - before,
                    "CommandsExecuted must grow by exactly N for N command dispatches.");
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void CompositeProcessing_NFires_IncrementsCompositeTriggersProcessedByExactlyN()
        {
            using var di = new NexusDI();
            var bus = CreateBus(di);
            try
            {
                bus.RegisterCompositeCommand(
                    new[] { typeof(BenchCompositeA), typeof(BenchCompositeB) },
                    typeof(BenchCompositeCommand),
                    oneShot: false, priority: 0, isAsync: false);

                int before = NexusProfiler.CompositeTriggersProcessed.Value;
                // With a composite trigger registered, every sync fire enters the composite
                // processing pass and must count exactly once.
                for (int i = 0; i < N; i++) bus.Fire(new BenchCompositeA());
                int after = NexusProfiler.CompositeTriggersProcessed.Value;

                Assert.AreEqual(N, after - before,
                    "CompositeTriggersProcessed must grow by exactly N for N fires while a composite trigger is registered.");
            }
            finally
            {
                bus.Dispose();
            }
        }

        [Test]
        public void Resolve_NTimes_IncrementsResolvesPerformedByExactlyN()
        {
            using var di = new NexusDI();
            try
            {
                di.Bind<BenchResolvable>(isSingleton: false);

                int before = NexusProfiler.ResolvesPerformed.Value;
                for (int i = 0; i < N; i++) di.Resolve<BenchResolvable>();
                int after = NexusProfiler.ResolvesPerformed.Value;

                Assert.AreEqual(N, after - before,
                    "ResolvesPerformed must grow by exactly N for N transient resolves.");
            }
            finally
            {
                di.Dispose();
            }
        }
    }
}
