using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.FSM;

namespace Nexus.Editor.Tests
{
    // Moved with the FSM from com.nexus.core's CasualFrameworkServicesTests (#112).
    [TestFixture]
    public class GameStateMachineTests
    {
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
    }
}
