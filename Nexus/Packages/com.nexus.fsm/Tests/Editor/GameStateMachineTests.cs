using NUnit.Framework;
using System;
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

        // Issue #192: a transition superseded while entering returned without exiting the state it entered, and its
        // successor exited the earlier state a second time.
        public abstract class Counted : IGameState
        {
            public int Enters, Exits;
            public virtual ValueTask OnEnterAsync(object args, CancellationToken ct) { Enters++; return default; }
            public ValueTask OnExitAsync(CancellationToken ct) { Exits++; return default; }
            public void OnTick(float deltaTime) { }
        }

        public sealed class FirstState : Counted { }
        public sealed class LastState : Counted { }

        public sealed class CancellableEnterState : Counted
        {
            public override async ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                Enters++;
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        public sealed class StubbornEnterState : Counted
        {
            public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                Enters++;
                await Release.Task; // ignores cancellation
            }
        }

        private static async Task Within(Task task, string what)
        {
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(5000)), what + " did not finish");
            await task;
        }

        [Test]
        public async Task ATransitionSupersededWhileEntering_ExitsTheStateItEntered_AndEachStateIsExitedOnce()
        {
            using var fsm = new GameStateMachine();
            var first = new FirstState();
            var entering = new CancellableEnterState();
            var last = new LastState();
            fsm.RegisterState(first);
            fsm.RegisterState(entering);
            fsm.RegisterState(last);
            await fsm.ChangeStateAsync<FirstState>();

            var superseded = fsm.ChangeStateAsync<CancellableEnterState>();
            var newest = fsm.ChangeStateAsync<LastState>();
            await Within(superseded, "the superseded transition");
            await Within(newest, "the newest transition");

            Assert.AreEqual(1, first.Exits, "the first state is exited once");
            Assert.AreEqual(1, entering.Enters);
            Assert.AreEqual(1, entering.Exits, "the state the superseded transition entered is exited");
            Assert.AreEqual(1, last.Enters);
            Assert.AreSame(last, fsm.CurrentState);
        }

        [Test]
        public async Task AnEnterThatIgnoresCancellation_IsExitedWhenItReturns_BeforeTheNewerStateEnters()
        {
            using var fsm = new GameStateMachine();
            var first = new FirstState();
            var stubborn = new StubbornEnterState();
            var last = new LastState();
            fsm.RegisterState(first);
            fsm.RegisterState(stubborn);
            fsm.RegisterState(last);
            await fsm.ChangeStateAsync<FirstState>();

            var superseded = fsm.ChangeStateAsync<StubbornEnterState>();
            var newest = fsm.ChangeStateAsync<LastState>();
            Assert.AreEqual(0, last.Enters, "the newer state waits for the transition it supersedes");
            stubborn.Release.SetResult(true);
            await Within(superseded, "the superseded transition");
            await Within(newest, "the newest transition");

            Assert.AreEqual(1, first.Exits, "the first state is exited once");
            Assert.AreEqual(1, stubborn.Exits, "the late-returning state is exited");
            Assert.AreEqual(1, last.Enters);
            Assert.AreSame(last, fsm.CurrentState);
        }

        [Test]
        public async Task DisposingDuringAnEnter_ExitsTheStateBeingEntered()
        {
            var fsm = new GameStateMachine();
            var first = new FirstState();
            var entering = new CancellableEnterState();
            fsm.RegisterState(first);
            fsm.RegisterState(entering);
            await fsm.ChangeStateAsync<FirstState>();

            var transition = fsm.ChangeStateAsync<CancellableEnterState>();
            fsm.Dispose();
            await Within(transition, "the transition cancelled by Dispose");

            Assert.AreEqual(1, first.Exits);
            Assert.AreEqual(1, entering.Exits, "the state being entered is exited");
            Assert.IsNull(fsm.CurrentState);
        }

        [Test]
        public void SetErrorState_RejectsAStateThatIsNotRegistered()
        {
            using var fsm = new GameStateMachine();

            var error = Assert.Throws<InvalidOperationException>(() => fsm.SetErrorState<LastState>());

            StringAssert.Contains("LastState is not registered", error.Message);
            Assert.IsNull(fsm.ErrorStateType);
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
