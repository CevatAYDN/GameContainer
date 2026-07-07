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
    }
}
