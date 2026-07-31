using NUnit.Framework;
using Nexus.Core;
using Nexus.Netcode;
using System.Threading;

namespace Nexus.Tests
{
    [TestFixture]
    [Ignore("bisect: temporarily excluded to isolate PlayMode hang poison")]
    public class NetcodeTests
    {
        private NexusDI _container;
        private CommandPoolManager _poolManager;
        private SignalBus _signalBus;
        private MockContext _context;
        private NetworkSignalBus _networkBus;
        private TestPlayerSnapshot _replayModel;

        public readonly struct TestPlayerSignal : INetworkSignal
        {
            public readonly int PlayerId;
            public readonly int Health;
            public TestPlayerSignal(int playerId, int health)
            {
                PlayerId = playerId;
                Health = health;
            }
        }

        public class TestPlayerSnapshot : ISnapshotableModel<TestPlayerSignal>
        {
            public int LastHealth;
            public TestPlayerSignal CaptureSnapshot() => new TestPlayerSignal(0, LastHealth);
            public void RestoreSnapshot(TestPlayerSignal state) => LastHealth = state.Health;
        }

        [SetUp]
        public void Setup()
        {
            _container = new NexusDI();
            _poolManager = new CommandPoolManager(_container);
            _context = new MockContext();
            _signalBus = new SignalBus(_container, _poolManager, _context);
            _networkBus = new NetworkSignalBus(_signalBus);
        }

        [TearDown]
        public void TearDown()
        {
            _signalBus.Dispose();
            _poolManager.Clear();
            _container.Dispose();
        }

        [Test]
        public void Fire_StoresTypedHistory_WithoutBoxing()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(1, history.Signals.Count);
            Assert.AreEqual(100, history.Signals[0].Signal.Health);
            Assert.AreEqual(0, history.Signals[0].Tick);
        }

        public class UpdateHealthCommand : ICommand<TestPlayerSignal>
        {
            [Inject] public TestPlayerSnapshot Model;
            public void Execute(TestPlayerSignal signal)
            {
                Model.LastHealth = signal.Health;
            }
        }

        [Test]
        public void RollbackAndResimulate_ReplaysTypedSignals_NoReflection()
        {
            var model = new TestPlayerSnapshot();
            _networkBus.RegisterModel(model);
            _container.BindInstance(model);
            _container.Bind<UpdateHealthCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TestPlayerSignal), typeof(UpdateHealthCommand), ExecutionMode.Sequential, 0, false);

            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(1, 80));

            _networkBus.RollbackAndResimulate(rollbackTick: 0, targetTick: 1);

            Assert.AreEqual(80, model.LastHealth);
        }

        [Test]
        public void PruneHistory_RemovesOldEntries()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(1, 90));
            _networkBus.SetTick(2);
            _networkBus.Fire(new TestPlayerSignal(1, 80));

            _networkBus.PruneHistory(confirmedTick: 1);

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(1, history.Signals.Count);
            Assert.AreEqual(80, history.Signals[0].Signal.Health);
        }

        [Test]
        public void Clear_RemovesAllHistoriesAndModels()
        {
            var model = new TestPlayerSnapshot();
            _networkBus.RegisterModel(model);
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 100));

            _networkBus.Clear();

            Assert.AreEqual(0, _networkBus.Histories.Count);
        }

        [Test]
        public void Prune_PreservesRelativeOrderOfSurvivors()
        {
            // Prune keeps signals strictly newer than the confirmed tick; survivors must
            // stay in chronological order after the O(N) in-place compaction rewrite.
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 10));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(2, 20));
            _networkBus.SetTick(2);
            _networkBus.Fire(new TestPlayerSignal(3, 30));
            _networkBus.SetTick(3);
            _networkBus.Fire(new TestPlayerSignal(4, 40));

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            history.Prune(confirmedTick: 1); // keeps ticks 2 and 3

            Assert.AreEqual(2, history.Signals.Count);
            Assert.AreEqual(30, history.Signals[0].Signal.Health);
            Assert.AreEqual(2, history.Signals[0].Tick, "First survivor is the tick-2 signal (health 30).");
            Assert.AreEqual(40, history.Signals[1].Signal.Health);
            Assert.AreEqual(3, history.Signals[1].Tick);
        }

        [Test]
        public void Prune_PrunesEverythingWhenAllOlderThanConfirmedTick()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 10));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(2, 20));

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            history.Prune(confirmedTick: 5);

            Assert.AreEqual(0, history.Signals.Count);
        }

        [Test]
        public void RemoveSignalsAfter_KeepsOnlyAtOrBeforeTick()
        {
            _networkBus.SetTick(0);
            _networkBus.Fire(new TestPlayerSignal(1, 10));
            _networkBus.SetTick(1);
            _networkBus.Fire(new TestPlayerSignal(2, 20));
            _networkBus.SetTick(2);
            _networkBus.Fire(new TestPlayerSignal(3, 30));

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            history.RemoveSignalsAfter(1); // drops the tick-2 signal

            Assert.AreEqual(2, history.Signals.Count);
            Assert.AreEqual(10, history.Signals[0].Signal.Health);
            Assert.AreEqual(20, history.Signals[1].Signal.Health);
        }

        // ─── Named-argument consistency (CS1739 regression guard) ──────────────
        // The Prune parameter rename (tick → confirmedTick) previously produced CS1739
        // at every named-argument call site. These two tests lock the contract: the
        // reflection check fails with a clear message on a rename; the compile-time
        // check fails the BUILD (CS1739) on a rename.

        private static void AssertParamName(System.Type type, string methodName, int index, string expected)
        {
            var m = type.GetMethod(methodName);
            Assert.IsNotNull(m, $"{type.Name}.{methodName} not found.");
            var p = m.GetParameters()[index];
            Assert.AreEqual(expected, p.Name,
                $"{type.Name}.{methodName} parameter #{index} should be named '{expected}' — " +
                "call sites using named arguments depend on this exact name.");
        }

        [Test]
        public void NetcodeApi_ParameterNames_MatchDocumentedNamedArguments()
        {
            AssertParamName(typeof(NetworkSignalBus), "SetTick", 0, "tick");
            AssertParamName(typeof(NetworkSignalBus), "FireAtTick", 0, "signal");
            AssertParamName(typeof(NetworkSignalBus), "FireAtTick", 1, "tick");
            AssertParamName(typeof(NetworkSignalBus), "RollbackAndResimulate", 0, "rollbackTick");
            AssertParamName(typeof(NetworkSignalBus), "RollbackAndResimulate", 1, "targetTick");
            AssertParamName(typeof(NetworkSignalBus), "PruneHistory", 0, "confirmedTick");

            AssertParamName(typeof(INetworkSignalHistory), "Prune", 0, "confirmedTick");
            AssertParamName(typeof(NetworkSignalHistory<>), "Prune", 0, "confirmedTick");
            AssertParamName(typeof(NetworkSignalHistory<>), "RemoveSignalsAfter", 0, "tick");
            AssertParamName(typeof(NetworkSignalHistory<>), "ReplaySignals", 0, "tick");
            AssertParamName(typeof(NetworkSignalHistory<>), "ReplaySignals", 1, "localSignalBus");

            AssertParamName(typeof(INetworkModelSnapshotHandler), "Prune", 0, "confirmedTick");
            AssertParamName(typeof(NetworkModelSnapshotHandler<>), "Capture", 0, "tick");
            AssertParamName(typeof(NetworkModelSnapshotHandler<>), "Restore", 0, "tick");
            AssertParamName(typeof(NetworkModelSnapshotHandler<>), "Prune", 0, "confirmedTick");
        }

        [Test]
        public void NetcodeApi_CallableWithNamedArguments()
        {
            // Compile-time guard: these exact calls must keep compiling. If a parameter
            // is renamed, this file stops compiling with CS1739 — the fastest possible
            // regression signal (this is exactly what broke during the Prune rename).
            _networkBus.SetTick(tick: 0);
            _networkBus.FireAtTick(signal: new TestPlayerSignal(1, 10), tick: 0);
            _networkBus.RollbackAndResimulate(rollbackTick: 0, targetTick: 0);
            _networkBus.PruneHistory(confirmedTick: 0);

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            history.ReplaySignals(tick: 0, localSignalBus: _signalBus);
            history.RemoveSignalsAfter(tick: 0);
            history.Prune(confirmedTick: 0);

            Assert.Pass("Netcode API accepts its documented named arguments.");
        }

        // ─── Replay consistency stress tests ───────────────────────────────────
        // Verify that RollbackAndResimulate stays deterministic and order-preserving
        // at scale, and that speculative future signals are pruned on rollback.

        private void RegisterReplayPipeline()
        {
            _replayModel = new TestPlayerSnapshot();
            _networkBus.RegisterModel(_replayModel);
            _container.BindInstance(_replayModel);
            _container.Bind<UpdateHealthCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TestPlayerSignal), typeof(UpdateHealthCommand), ExecutionMode.Sequential, 0, false);
        }

        [Test]
        public void Rollback_Stress_200Ticks_DeterministicReplay()
        {
            RegisterReplayPipeline();

            const int tickCount = 200;
            for (int t = 0; t < tickCount; t++)
            {
                _networkBus.SetTick(t);
                _networkBus.Fire(new TestPlayerSignal(1, t * 10));
            }
            Assert.AreEqual((tickCount - 1) * 10, _replayModel.LastHealth,
                "Pre-rollback state must be the last fired signal.");

            // Roll back to tick 50 and resimulate to the end of the confirmed horizon.
            _networkBus.RollbackAndResimulate(rollbackTick: 50, targetTick: tickCount - 1);

            // Deterministic replay: model must be exactly the last replayed signal value.
            Assert.AreEqual((tickCount - 1) * 10, _replayModel.LastHealth,
                "After rollback + resimulation the model must equal the last replayed signal.");

            // History must retain every signal 0..tickCount-1 in chronological order.
            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(tickCount, history.Signals.Count);
            for (int i = 0; i < history.Signals.Count; i++)
            {
                Assert.AreEqual(i, history.Signals[i].Tick,
                    $"History order corrupted at index {i} after rollback.");
                Assert.AreEqual(i * 10, history.Signals[i].Signal.Health);
            }
        }

        [Test]
        public void Rollback_PrunesFuturePredictionSignals()
        {
            RegisterReplayPipeline();

            // Confirmed horizon: ticks 0..100.
            for (int t = 0; t <= 100; t++)
            {
                _networkBus.SetTick(t);
                _networkBus.Fire(new TestPlayerSignal(1, t * 10));
            }

            // A speculative/predicted signal beyond the confirmed horizon.
            _networkBus.SetTick(120);
            _networkBus.Fire(new TestPlayerSignal(1, 9999));
            Assert.AreEqual(9999, _replayModel.LastHealth);

            _networkBus.RollbackAndResimulate(rollbackTick: 40, targetTick: 100);

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(101, history.Signals.Count, "Future prediction (tick 120) must be pruned.");
            Assert.AreEqual(100, history.Signals[100].Tick);
            Assert.AreEqual(100 * 10, _replayModel.LastHealth,
                "Model must resimulate exactly to the confirmed horizon (tick 100).");
        }

        [Test]
        public void Rollback_RepeatRollback_IsDeterministic()
        {
            RegisterReplayPipeline();

            for (int t = 0; t <= 100; t++)
            {
                _networkBus.SetTick(t);
                _networkBus.Fire(new TestPlayerSignal(1, t * 7 + 3));
            }

            _networkBus.RollbackAndResimulate(rollbackTick: 20, targetTick: 80);
            int firstPass = _replayModel.LastHealth;
            Assert.AreEqual(80 * 7 + 3, firstPass);

            // Rolling back to the same point twice must yield the identical state and
            // must not corrupt the retained history.
            _networkBus.RollbackAndResimulate(rollbackTick: 20, targetTick: 80);
            Assert.AreEqual(firstPass, _replayModel.LastHealth,
                "Repeated rollback must be deterministic.");

            var history = (NetworkSignalHistory<TestPlayerSignal>)_networkBus.Histories[typeof(TestPlayerSignal)];
            Assert.AreEqual(81, history.Signals.Count, "History must stay intact across repeated rollbacks.");
        }

        // ─── Prune steady-state allocation measurement ─────────────────────────
        // Regression guard for the O(N²)→O(N) compaction rewrite: the in-place write-
        // index compaction + tail RemoveRange must not allocate in steady state.

        [Test]
        public void Prune_SteadyState_ZeroAllocations()
        {
            var history = new NetworkSignalHistory<TestPlayerSignal>();
            const int fillCount = 1000;

            // Warm up: grow List capacity once and JIT the compaction loops.
            for (int i = 0; i < fillCount; i++) history.Add(i, new TestPlayerSignal(1, i));
            for (int w = 0; w < 5; w++)
            {
                history.Prune(0);
                history.Prune(int.MaxValue);
                for (int i = 0; i < fillCount; i++) history.Add(fillCount + i, new TestPlayerSignal(1, i));
                history.Prune(int.MaxValue);
            }

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long startAllocations = System.GC.GetAllocatedBytesForCurrentThread();

            // Steady state: clear the previous tail (capacity stays), refill within
            // existing capacity (free), then compact down to a small tail. This exercises
            // both the partial compaction loop and the full tail RemoveRange.
            int tick = 10000;
            const int cycles = 500;
            for (int iter = 0; iter < cycles; iter++)
            {
                history.Prune(int.MaxValue);
                for (int i = 0; i < fillCount; i++)
                    history.Add(tick++, new TestPlayerSignal(1, i));
                history.Prune(tick - 10); // keeps the last 9 signals (Tick > tick-10)
            }

            long allocatedBytes = System.GC.GetAllocatedBytesForCurrentThread() - startAllocations;

            // BufferedNetworkSignal<T> is a struct, List.Add is free within capacity,
            // and RemoveRange on a reference-free struct list only adjusts Count. So
            // 500 cycles × (1000 adds + 2 prunes) must stay at ~0 bytes.
            Assert.LessOrEqual(allocatedBytes, 512,
                $"Prune steady state allocated {allocatedBytes} bytes over {cycles} cycles. Expected ~0.");
        }
    }
}
