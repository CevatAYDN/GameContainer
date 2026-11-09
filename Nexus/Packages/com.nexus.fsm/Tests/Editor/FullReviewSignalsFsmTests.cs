using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.FSM;
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine.TestTools;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewSignalsFsmTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public async Task S06_OrdinaryEnterFailure_ReleasesItsSubscription()
        {
            using var errors = new ExpectedErrors();
            using var context = new Context();
            using var machine = new GameStateMachine();
            var state = new FailingState(context.SignalBus);
            machine.RegisterState(state);
            await machine.ChangeStateAsync<FailingState>();
            context.SignalBus.Fire(new Probe());
            Require(state.Exits == 1 && state.Calls == 0 && !state.ExitTokenWasCancelled && machine.CurrentState == null,
                $"S06: failed entry must release its subscription once; exits={state.Exits}, calls={state.Calls}");
            machine.Dispose();
            Require(state.Exits == 1, "S06: disposal must not exit the failed state twice");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task S06_FallbackEnterFailure_ReleasesBothSubscriptions()
        {
            using var errors = new ExpectedErrors();
            using var context = new Context();
            using var machine = new GameStateMachine();
            var state = new FailingState(context.SignalBus);
            var fallback = new FailingFallback(context.SignalBus);
            machine.RegisterState(state);
            machine.RegisterState(fallback);
            machine.SetErrorState<FailingFallback>();
            await machine.ChangeStateAsync<FailingState>();
            context.SignalBus.Fire(new Probe());
            Require(state.Exits == 1 && fallback.Exits == 1 && state.Calls == 0 && fallback.Calls == 0 && machine.CurrentState == null,
                $"S06: both failed entries need cleanup; exits={state.Exits}/{fallback.Exits}, calls={state.Calls}/{fallback.Calls}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task S06_NewTransitionDuringFailureCleanup_SuppressesStaleFallback()
        {
            using var errors = new ExpectedErrors();
            using var context = new Context();
            using var machine = new GameStateMachine();
            var state = new FailingState(context.SignalBus) { HoldExit = true };
            var fallback = new ReadyFallback();
            var latest = new ReadyState();
            machine.RegisterState(state);
            machine.RegisterState(fallback);
            machine.RegisterState(latest);
            machine.SetErrorState<ReadyFallback>();
            var pending = machine.ChangeStateAsync<FailingState>();
            try
            {
                await Within(Task.WhenAny(state.ExitStarted.Task, pending));
                Require(state.ExitStarted.Task.IsCompleted && !pending.IsCompleted && fallback.Enters == 0,
                    $"S06: fallback must wait for partial-entry cleanup; exits={state.Exits}, fallbackEnters={fallback.Enters}");
                var next = machine.ChangeStateAsync<ReadyState>();
                state.ExitRelease.TrySetResult(true);
                await Within(Task.WhenAll(pending, next));
                context.SignalBus.Fire(new Probe());
                Require(state.Exits == 1 && state.Calls == 0 && !state.ExitTokenWasCancelled
                    && fallback.Enters == 0 && latest.Enters == 1 && machine.CurrentState == latest,
                    "S06: only the latest state may enter after failed-entry cleanup");
            }
            finally
            {
                state.ExitRelease.TrySetResult(true);
                await Within(pending);
            }
        }

        private readonly struct Probe { }

        private class FailingState : IGameState
        {
            private readonly ISignalBus _bus;
            private ISignalSubscription _subscription;
            public readonly TaskCompletionSource<bool> ExitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ExitRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Exits, Calls;
            public bool HoldExit, ExitTokenWasCancelled;
            public FailingState(ISignalBus bus) => _bus = bus;
            public ValueTask OnEnterAsync(object args, CancellationToken ct)
            {
                _subscription = _bus.Subscribe<Probe>(_ => Calls++);
                throw new InvalidOperationException("S06 failed after acquiring a subscription");
            }
            public async ValueTask OnExitAsync(CancellationToken ct)
            {
                Exits++;
                ExitTokenWasCancelled = ct.IsCancellationRequested;
                ExitStarted.TrySetResult(true);
                if (HoldExit) await ExitRelease.Task;
                _subscription.Dispose();
            }
            public void OnTick(float deltaTime) { }
        }

        private sealed class FailingFallback : FailingState
        {
            public FailingFallback(ISignalBus bus) : base(bus) { }
        }

        private class ReadyState : IGameState
        {
            public int Enters;
            public ValueTask OnEnterAsync(object args, CancellationToken ct) { Enters++; return default; }
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }

        private sealed class ReadyFallback : ReadyState { }

        private static async Task Within(Task task)
        {
            Require(await Task.WhenAny(task, Task.Delay(5000)) == task, "S06: transition did not finish in 5 s");
            await task;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class ExpectedErrors : IDisposable
        {
#if UNITY_EDITOR
            private readonly bool _previous = LogAssert.ignoreFailingMessages;
            public ExpectedErrors() => LogAssert.ignoreFailingMessages = true;
            public void Dispose() => LogAssert.ignoreFailingMessages = _previous;
#else
            public void Dispose() { }
#endif
        }
    }
}
