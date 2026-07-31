using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.FSM;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class CasualFrameworkServicesTests
    {
        [Test]
        public void ObservableProperty_FiresMultipleHandlersWithoutAllocating()
        {
            var prop = new ObservableProperty<int>(10);
            int callCount1 = 0;
            int callCount2 = 0;

            prop.OnChanged((oldVal, newVal) => callCount1++);
            prop.OnChanged((oldVal, newVal) => callCount2++);

            prop.Value = 20;

            Assert.AreEqual(1, callCount1);
            Assert.AreEqual(1, callCount2);
            Assert.AreEqual(20, prop.Value);
        }

        [Test]
        public void ObservableList_MulticastCallbacksWorkCorrectly()
        {
            var list = new ObservableList<string>();
            int addedCalls1 = 0;
            int addedCalls2 = 0;

            list.OnAdded((idx, item) => addedCalls1++);
            list.OnAdded((idx, item) => addedCalls2++);

            list.Add("Item1");

            Assert.AreEqual(1, addedCalls1);
            Assert.AreEqual(1, addedCalls2);
            Assert.AreEqual(1, list.Count);
        }

        public class MockStateA : IGameState
        {
            public bool IsEntered { get; private set; }
            public bool IsExited { get; private set; }

            public ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                IsEntered = true;
                return default;
            }

            public ValueTask OnExitAsync(CancellationToken ct)
            {
                IsExited = true;
                return default;
            }

            public void OnTick(float deltaTime) { }
        }

        public class MockStateB : IGameState
        {
            public bool IsEntered { get; private set; }
            public bool IsExited { get; private set; }

            public ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                IsEntered = true;
                return default;
            }

            public ValueTask OnExitAsync(CancellationToken ct)
            {
                IsExited = true;
                return default;
            }

            public void OnTick(float deltaTime) { }
        }

        // A state whose OnExitAsync is slow and IGNORES cancellation. This is the worst
        // case for a concurrent transition: the machine must still drop the superseded
        // transition via its sequence check, even though the state never cooperates.
        public class MockStateSlowExit : IGameState
        {
            public bool IsEntered { get; private set; }

            public ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                IsEntered = true;
                return default;
            }

            public async ValueTask OnExitAsync(CancellationToken ct)
            {
                await Task.Delay(30);
            }

            public void OnTick(float deltaTime) { }
        }

        [Test]
        public async Task GameStateMachine_TransitionsBetweenStates()
        {
            using var fsm = new GameStateMachine();
            var stateA = new MockStateA();
            var stateB = new MockStateB();

            fsm.RegisterState(stateA);
            fsm.RegisterState(stateB);

            await fsm.ChangeStateAsync<MockStateA>();
            Assert.IsTrue(stateA.IsEntered);
            Assert.AreSame(stateA, fsm.CurrentState);

            await fsm.ChangeStateAsync<MockStateB>();
            Assert.IsTrue(stateA.IsExited);
            Assert.IsTrue(stateB.IsEntered);
            Assert.AreSame(stateB, fsm.CurrentState);
        }

        [Test]
        public async Task GameStateMachine_ConcurrentChangeState_SupersedesWithoutCorruption()
        {
            using var fsm = new GameStateMachine();
            var slow = new MockStateSlowExit();
            var stateA = new MockStateA();
            var stateB = new MockStateB();

            fsm.RegisterState(slow);
            fsm.RegisterState(stateA);
            fsm.RegisterState(stateB);

            await fsm.ChangeStateAsync<MockStateSlowExit>();
            Assert.AreSame(slow, fsm.CurrentState);

            // Fire A, then immediately supersede it with B while A's transition is still
            // awaiting the slow OnExitAsync. Exactly one state may end up current, and
            // the superseded A must never be entered.
            var t1 = fsm.ChangeStateAsync<MockStateA>();
            var t2 = fsm.ChangeStateAsync<MockStateB>();

            await t2;
            await t1;

            Assert.AreSame(stateB, fsm.CurrentState,
                "The newest transition wins; the superseded one must not clobber _currentState.");
            Assert.IsFalse(stateA.IsEntered, "Superseded transition must never run OnEnterAsync.");
            Assert.IsTrue(stateB.IsEntered);
        }

        [Test]
        public void EconomyService_EarnSpendAndCanAffordLogic()
        {
            using var eco = new EconomyService();
            eco.SetBalance("Coins", 100);

            Assert.AreEqual(100, eco.GetBalance("Coins"));
            Assert.IsTrue(eco.CanAfford("Coins", 50));
            Assert.IsFalse(eco.CanAfford("Coins", 150));

            bool spent = eco.Spend("Coins", 40);
            Assert.IsTrue(spent);
            Assert.AreEqual(60, eco.GetBalance("Coins"));

            eco.Earn("Coins", 30);
            Assert.AreEqual(90, eco.GetBalance("Coins"));
        }

        // TCS-based validator: the test controls WHEN the network validation completes.
        // Task.FromResult would return an already-completed task, which makes the await in
        // ReconcileSpendAsync continue synchronously — the rollback would run BEFORE Spend
        // returns, so the optimistic intermediate balance would never be observable.
        private class FakeNetworkValidator : INetworkEconomyValidator
        {
            public TaskCompletionSource<bool> SpendResult = new();
            public Task<bool> ValidateSpendAsync(string currencyId, long amount, string reason)
                => SpendResult.Task;
            public Task ValidateEarnAsync(string currencyId, long amount, string reason)
                => Task.CompletedTask;
        }

        [Test]
        public void EconomyService_Earn_ClampsAtLongMax_NoOverflow()
        {
            using var eco = new EconomyService();
            eco.SetBalance("Coins", long.MaxValue - 10);

            eco.Earn("Coins", 100);

            Assert.AreEqual(long.MaxValue, eco.GetBalance("Coins"),
                "Earn must clamp at long.MaxValue instead of wrapping negative.");
        }

        [Test]
        public void EconomyService_Earn_NegativeOrZeroAmountIgnored()
        {
            using var eco = new EconomyService();
            eco.SetBalance("Coins", 100);

            eco.Earn("Coins", -50);
            eco.Earn("Coins", 0);

            Assert.AreEqual(100, eco.GetBalance("Coins"));
        }

        [Test]
        public async Task EconomyService_ServerRejectedSpend_RestoresBalance()
        {
            var validator = new FakeNetworkValidator();
            using var eco = new EconomyService { NetworkValidator = validator };
            eco.SetBalance("Coins", 100);

            bool spent = eco.Spend("Coins", 40);
            Assert.IsTrue(spent);
            Assert.AreEqual(60, eco.GetBalance("Coins"), "Optimistic local deduction applies immediately.");

            // Reject the spend: the fire-and-forget reconciliation restores the amount.
            validator.SpendResult.SetResult(false);
            await Task.Delay(50);

            Assert.AreEqual(100, eco.GetBalance("Coins"),
                "Server-rejected spend must be rolled back to keep client/server in sync.");
        }

        [Test]
        public async Task EconomyService_ServerApprovedSpend_KeepsDeduction()
        {
            var validator = new FakeNetworkValidator();
            using var eco = new EconomyService { NetworkValidator = validator };
            eco.SetBalance("Coins", 100);

            eco.Spend("Coins", 40);
            validator.SpendResult.SetResult(true);
            await Task.Delay(50);

            Assert.AreEqual(60, eco.GetBalance("Coins"),
                "Approved spend must NOT be rolled back.");
        }

        [Test]
        public void EconomyService_GetObservableBalance_ReturnsSecureStorage()
        {
            using var eco = new EconomyService();
            eco.SetBalance("Gems", 12345L);

            var prop = eco.GetObservableBalance("Gems");
            Assert.IsInstanceOf<SecureObservableLong>(prop);
            Assert.AreEqual(12345L, prop.Value);
        }

        [Test]
        public void ProgressionService_LevelProgressionAndCostCalculations()
        {
            using var prog = new ProgressionService();
            prog.SetLevel(1);

            Assert.AreEqual(1, prog.CurrentLevel.Value);
            prog.CompleteCurrentLevel();
            Assert.AreEqual(2, prog.CurrentLevel.Value);
            Assert.AreEqual(2, prog.MaxUnlockedLevel.Value);

            long baseCost = 100;
            long expCostLvl1 = prog.CalculateUpgradeCost(baseCost, 1, 1.5f, CurveType.Exponential);
            long expCostLvl2 = prog.CalculateUpgradeCost(baseCost, 2, 1.5f, CurveType.Exponential);
            long expCostLvl3 = prog.CalculateUpgradeCost(baseCost, 3, 1.5f, CurveType.Exponential);

            Assert.AreEqual(100, expCostLvl1);
            Assert.AreEqual(150, expCostLvl2);
            Assert.AreEqual(225, expCostLvl3);
        }

        [Test]
        public void ProgressionService_UpgradeCost_ClampsInsteadOfOverflowing()
        {
            using var prog = new ProgressionService();

            // 100 * 1.15^1999 is astronomically past long.MaxValue; the old unchecked
            // (long) cast wrapped to long.MinValue. Must clamp instead.
            Assert.AreEqual(long.MaxValue, prog.CalculateUpgradeCost(100, 2000, 1.15f, CurveType.Exponential));

            // 100 * 2000^10 is also way beyond long range → clamp, not wrap.
            Assert.AreEqual(long.MaxValue, prog.CalculateUpgradeCost(100, 2000, 10f, CurveType.Polynomial));

            // Linear with multiplier < 1 goes negative on raw math; cost must never drop
            // below the base cost (and never go negative).
            long lin = prog.CalculateUpgradeCost(100, 50, 0.5f, CurveType.Linear);
            Assert.GreaterOrEqual(lin, 100);

            // Ordinary values still follow the documented curves.
            Assert.AreEqual(150, prog.CalculateUpgradeCost(100, 2, 1.5f, CurveType.Exponential));
            Assert.AreEqual(225, prog.CalculateUpgradeCost(100, 3, 1.5f, CurveType.Exponential));
        }
    }
}
