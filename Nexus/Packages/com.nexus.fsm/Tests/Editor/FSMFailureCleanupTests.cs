using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.FSM;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FSMFailureCleanupTests
    {
        private sealed class LateFailure : IGameState
        {
            public readonly TaskCompletionSource<bool> EnterRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ExitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ExitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool HoldExit, ExitCompleted, ExitTokenWasCancelled;
            public int Exits;
            public async ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                await EnterRelease.Task;
                throw new InvalidOperationException("late failure after supersession");
            }
            public async ValueTask OnExitAsync(CancellationToken ct)
            {
                Exits++;
                ExitTokenWasCancelled = ct.IsCancellationRequested;
                ExitStarted.TrySetResult(true);
                if (HoldExit) await ExitRelease.Task;
                ExitCompleted = true;
            }
            public void OnTick(float deltaTime) { }
        }

        private class Ready : IGameState
        {
            public int Enters;
            public ValueTask OnEnterAsync(object args, CancellationToken ct) { Enters++; return default; }
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }
        private sealed class Last : Ready { }
        private sealed class Fallback : Ready { }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static async Task Within(Task task)
        {
            Require(await Task.WhenAny(task, Task.Delay(5000)) == task, "transition/cleanup did not finish in 5 s");
            await task;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task SupersededLateFailure_ExitsOnce_WithoutEnteringFallback()
        {
            using var machine = new GameStateMachine();
            var old = new LateFailure();
            var next = new Ready();
            var fallback = new Fallback();
            machine.RegisterState(old);
            machine.RegisterState(next);
            machine.RegisterState(fallback);
            machine.SetErrorState<Fallback>();
            var previous = machine.ChangeStateAsync<LateFailure>();
            var latest = machine.ChangeStateAsync<Ready>();
            old.EnterRelease.SetResult(true);
            await Within(Task.WhenAll(previous, latest));
            Require(old.Exits == 1 && old.ExitCompleted && !old.ExitTokenWasCancelled,
                $"superseded failure needs one completed exit on a fresh token, got {old.Exits}");
            Require(machine.CurrentState == next && next.Enters == 1 && fallback.Enters == 0,
                "the newest state must own the machine, not a stale error fallback");
            machine.Dispose();
            Require(old.Exits == 1, "later disposal must not exit the failed state again");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task DisposeDuringLateFailure_ExitsOnce_AndLeavesNoCurrentState()
        {
            using var machine = new GameStateMachine();
            var old = new LateFailure();
            machine.RegisterState(old);
            var transition = machine.ChangeStateAsync<LateFailure>();
            machine.Dispose();
            old.EnterRelease.SetResult(true);
            await Within(transition);
            Require(old.Exits == 1 && old.ExitCompleted && !old.ExitTokenWasCancelled && machine.CurrentState == null,
                $"dispose must clean the late-failing enter once, got {old.Exits} exits");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task LateFailureExit_IsAwaited_BeforeTheNewestStateEnters()
        {
            using var machine = new GameStateMachine();
            var old = new LateFailure { HoldExit = true };
            var next = new Ready();
            var last = new Last();
            machine.RegisterState(old);
            machine.RegisterState(next);
            machine.RegisterState(last);
            var previous = machine.ChangeStateAsync<LateFailure>();
            var superseded = machine.ChangeStateAsync<Ready>();
            try
            {
                old.EnterRelease.SetResult(true);
                await Within(old.ExitStarted.Task);
                Require(!superseded.IsCompleted && next.Enters == 0, "the successor entered before failed-state cleanup");
                var latest = machine.ChangeStateAsync<Last>();
                old.ExitRelease.SetResult(true);
                await Within(Task.WhenAll(previous, superseded, latest));
                Require(old.Exits == 1 && old.ExitCompleted && next.Enters == 0 && last.Enters == 1 && machine.CurrentState == last,
                    "cleanup must finish once, then only the newest transition may enter");
            }
            finally { old.ExitRelease.TrySetResult(true); }
        }

        private sealed class Held : IGameState
        {
            public readonly TaskCompletionSource<bool> EnterRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Exits;
            public async ValueTask OnEnterAsync(object args, CancellationToken ct) => await EnterRelease.Task;
            public ValueTask OnExitAsync(CancellationToken ct) { Exits++; return default; }
            public void OnTick(float deltaTime) { }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task PreCancelledChange_LeavesTheRunningTransitionAlone()
        {
            using var machine = new GameStateMachine();
            var held = new Held();
            var next = new Ready();
            machine.RegisterState(held);
            machine.RegisterState(next);
            var running = machine.ChangeStateAsync<Held>();
            try
            {
                await Within(machine.ChangeStateAsync(typeof(Ready), new CancellationToken(true)));
                held.EnterRelease.SetResult(true);
                await Within(running);
                Require(machine.CurrentState == held && held.Exits == 0 && next.Enters == 0,
                    "a change whose token was already cancelled must not cancel the transition in progress (#423)");
            }
            finally { held.EnterRelease.TrySetResult(true); }
        }
    }
}
