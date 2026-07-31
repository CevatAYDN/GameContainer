using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.FSM;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class FSMTransitionHistoryTests
    {
        private class MockStateA : IGameState
        {
            public ValueTask OnEnterAsync(object args, CancellationToken ct) => default;
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }

        private class MockStateB : IGameState
        {
            public ValueTask OnEnterAsync(object args, CancellationToken ct) => default;
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }

        private class MockStateSlowExit : IGameState
        {
            // Deliberately IGNORES cancellation: the machine must still drop a superseded
            // transition via its sequence check even when the state never cooperates.
            public async ValueTask OnExitAsync(CancellationToken ct) => await Task.Delay(30);
            public ValueTask OnEnterAsync(object args, CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }

        [Test]
        public async Task FSM_RecordsTransitionsAndFiresOnStateChanged()
        {
            using var fsm = new GameStateMachine();
            var stateA = new MockStateA();
            var stateB = new MockStateB();
            fsm.RegisterState(stateA);
            fsm.RegisterState(stateB);

            int fired = 0;
            StateTransitionRecord last = default;
            fsm.OnStateChanged += r => { fired++; last = r; };

            await fsm.ChangeStateAsync<MockStateA>("hello");

            Assert.AreEqual(1, fired, "OnStateChanged must fire once per transition.");
            Assert.AreEqual(StateTransitionStatus.Success, last.Status);
            Assert.IsNull(last.FromState, "First transition has no source state.");
            Assert.AreEqual("MockStateA", last.ToState);
            Assert.AreEqual("String", last.ArgsSummary, "Args are summarized by type name, never ToString().");
            Assert.GreaterOrEqual(last.DurationMs, 0d);
            Assert.Greater(last.Timestamp, 0d);

            await fsm.ChangeStateAsync<MockStateB>();

            Assert.AreEqual(2, fired);
            Assert.AreEqual("MockStateA", last.FromState);
            Assert.AreEqual("MockStateB", last.ToState);
            Assert.AreEqual(StateTransitionStatus.Success, last.Status);
            Assert.AreEqual(2, fsm.TransitionCount);
            Assert.AreEqual(2, fsm.GetRecentTransitions().Count);
        }

        [Test]
        public async Task FSM_RingBufferWrapsAndOverwritesOldest()
        {
            using var fsm = new GameStateMachine();
            fsm.RegisterState(new MockStateA());
            fsm.RegisterState(new MockStateB());

            // Alternating A/B transitions — far more than the 32-slot ring buffer.
            for (int i = 0; i < 40; i++)
            {
                await fsm.ChangeStateAsync<MockStateA>();
                await fsm.ChangeStateAsync<MockStateB>();
            }

            Assert.AreEqual(GameStateMachine.TransitionHistoryCapacity, fsm.TransitionCount,
                "Count must cap at ring-buffer capacity.");
            Assert.AreEqual(32, fsm.GetRecentTransitions().Count);

            var recent = fsm.GetRecentTransitions();
            Assert.AreEqual(StateTransitionStatus.Success, recent[recent.Count - 1].Status);
            Assert.AreEqual("MockStateB", recent[recent.Count - 1].ToState,
                "The newest transition must be preserved (B was last).");
            Assert.AreEqual("MockStateB", recent[0].FromState,
                "Oldest surviving record must be the 49th transition (A from B), not the first.");
            Assert.AreEqual("MockStateA", recent[0].ToState);
        }

        [Test]
        public async Task FSM_SupersededTransitionIsRecordedAndSkipped()
        {
            using var fsm = new GameStateMachine();
            var slow = new MockStateSlowExit();
            var stateA = new MockStateA();
            var stateB = new MockStateB();
            fsm.RegisterState(slow);
            fsm.RegisterState(stateA);
            fsm.RegisterState(stateB);

            await fsm.ChangeStateAsync<MockStateSlowExit>();

            // Fire A, then immediately supersede it with B while A's transition is still
            // awaiting the slow OnExitAsync.
            var t1 = fsm.ChangeStateAsync<MockStateA>();
            var t2 = fsm.ChangeStateAsync<MockStateB>();
            await t2;
            await t1;

            Assert.AreSame(stateB, fsm.CurrentState);
            // THREE records, not two: the initial null→SlowExit transition is recorded too
            // (proven by FSM_RecordsTransitionsAndFiresOnStateChanged, where the first
            // ChangeStateAsync yields TransitionCount == 1). History is therefore:
            //   [0] Success    null→MockStateSlowExit
            //   [1] Superseded MockStateSlowExit→MockStateA   (preempted by B)
            //   [2] Success    MockStateSlowExit→MockStateB
            Assert.AreEqual(3, fsm.TransitionCount,
                "The initial transition, the superseded A attempt, and the successful B transition are all recorded.");

            var recent = fsm.GetRecentTransitions();
            Assert.AreEqual(StateTransitionStatus.Success, recent[0].Status);
            Assert.AreEqual("MockStateSlowExit", recent[0].ToState);
            Assert.AreEqual(StateTransitionStatus.Superseded, recent[1].Status);
            Assert.AreEqual("MockStateSlowExit", recent[1].FromState);
            Assert.AreEqual("MockStateA", recent[1].ToState);
            Assert.AreEqual(StateTransitionStatus.Success, recent[2].Status);
            Assert.AreEqual("MockStateB", recent[2].ToState);
        }

        [Test]
        public async Task FSM_FailedTransitionFallsBackToErrorStateAndIsRecorded()
        {
            using var fsm = new GameStateMachine();
            fsm.SetErrorState<MockStateB>();
            fsm.RegisterState(new ThrowingState());
            fsm.RegisterState(new MockStateB());

            await fsm.ChangeStateAsync<ThrowingState>();

            var recent = fsm.GetRecentTransitions();
            Assert.AreEqual(1, recent.Count);
            Assert.AreEqual(StateTransitionStatus.Failed, recent[0].Status);
            Assert.AreEqual("MockStateB", recent[0].ToState,
                "Failed transition must record the error state it fell back to.");
            Assert.AreEqual("MockStateB", fsm.CurrentState?.GetType().Name);
        }

        private class ThrowingState : IGameState
        {
            public ValueTask OnEnterAsync(object args, CancellationToken ct)
                => throw new System.InvalidOperationException("boom");
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }
    }
}
