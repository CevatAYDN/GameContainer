using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine.TestTools;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewSignalsCommandTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void S01_SynchronousComposite_StopsAtItsRetryLimit()
        {
            using var errors = new ExpectedErrors();
            using var di = new NexusDI();
            int attempts = 0;
            var decisions = new List<int>();
            di.BindFactory(() => new Composite(() =>
            {
                if (++attempts <= 3) throw new InvalidOperationException("S01 bounded failure");
            }));
            di.BindInstance<IRecoveryStrategy>(new Strategy(failure =>
            {
                decisions.Add(failure.RetryCount);
                return RecoveryDecision.Retry(1);
            }));
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCompositeCommand(new[] { typeof(First), typeof(Second) }, typeof(Composite), false, 0, false);
            bus.Fire(new First());
            Exception failure = null;
            try { bus.Fire(new Second()); }
            catch (Exception ex) { failure = ex; }
            Require(failure is NexusRecoveryAbortException && attempts == 2
                && string.Join(",", decisions) == "0,1",
                $"S01: expected two attempts then abort; attempts={attempts}, decisions={string.Join(",", decisions)}, failure={failure}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task S02_ThrowingCancellation_StillDisposesEverySubscriptionAndRegistration()
        {
            using var errors = new ExpectedErrors();
            using var di = new NexusDI();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int cancellations = 0;
            di.BindFactory(() => new AsyncCommand(async ct =>
            {
                using var callback = ct.Register(() =>
                {
                    cancellations++;
                    throw new InvalidOperationException("S02 cancellation callback");
                });
                await release.Task;
                ct.ThrowIfCancellationRequested();
            }));
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCommand(typeof(First), typeof(AsyncCommand), ExecutionMode.Sequential, 0, true);
            using var subscription = bus.Subscribe<Second>(_ => { });
            var pending = bus.FireAsync(new First()).AsTask();
            Exception disposalFailure = null;
            bool active;
            int registrations;
            try
            {
                try { bus.Dispose(); }
                catch (Exception ex) { disposalFailure = ex; }
                active = subscription.IsActive;
                registrations = bus.RegisteredHandlers.Count;
                bus.Dispose();
            }
            finally
            {
                release.TrySetResult(true);
                try { await pending; }
                catch (OperationCanceledException) { }
            }
            Require(cancellations == 1 && !active && registrations == 0,
                $"S02: cancellation must not skip cleanup; callbacks={cancellations}, active={active}, registrations={registrations}, failure={disposalFailure}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void S04_SyncFallbackDepth_AbortsAndReleasesTheOneShot()
            => CheckFallbackDepth(false).GetAwaiter().GetResult();

#if UNITY_EDITOR
        [Test]
#endif
        public Task S04_AsyncFallbackDepth_AbortsAndReleasesTheOneShot()
            => CheckFallbackDepth(true);

        private static async Task CheckFallbackDepth(bool asynchronous)
        {
            using var errors = new ExpectedErrors();
            using var di = new NexusDI();
            int attempts = 0, fallbacks = 0, deliveries = 0;
            Action fail = () => { attempts++; throw new InvalidOperationException("S04 primary"); };
            Action fallback = () => { fallbacks++; throw new InvalidOperationException("S04 fallback"); };
            di.BindFactory(() => new SyncCommand(fail));
            di.BindFactory(() => new AsyncCommand(_ => { fail(); return default; }));
            di.BindFactory(() => new Fallback(fallback));
            di.BindFactory(() => new AsyncFallback(_ => { fallback(); return default; }));
            di.BindInstance<IRecoveryStrategy>(new Strategy(_ => asynchronous
                ? RecoveryDecision.FallbackAsync<AsyncFallback>()
                : RecoveryDecision.Fallback<Fallback>()));
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCommand(typeof(First), asynchronous ? typeof(AsyncCommand) : typeof(SyncCommand),
                ExecutionMode.Sequential, 0, asynchronous, oneShot: true);
            bus.Subscribe<First>(_ => deliveries++);
            for (int fire = 1; fire <= 2; fire++)
            {
                Exception failure = null;
                try
                {
                    if (asynchronous) await bus.FireAsync(new First());
                    else bus.Fire(new First());
                }
                catch (Exception ex) { failure = ex; }
                Require(failure is NexusRecoveryAbortException && deliveries == 0
                    && attempts == fire && fallbacks == fire * 3 && bus.HasCommandHandler<First>(),
                    $"S04: depth exhaustion must abort, not consume the one-shot; async={asynchronous}, fire={fire}, attempts={attempts}, fallbacks={fallbacks}, deliveries={deliveries}, failure={failure}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public Task S05_AsyncComposite_HonorsItsCommandTimeout() => CheckCommandTimeout(0);

#if UNITY_EDITOR
        [Test]
#endif
        public Task S05_NonGenericAsyncComposite_HonorsItsCommandTimeout() => CheckCommandTimeout(1);

#if UNITY_EDITOR
        [Test]
#endif
        public Task S05_AsyncFallback_HonorsItsCommandTimeout() => CheckCommandTimeout(2);

        private static async Task CheckCommandTimeout(int route)
        {
            using var errors = new ExpectedErrors();
            using var di = new NexusDI();
            di.BindFactory(() => new TimedComposite());
            di.BindFactory(() => new TimedFallback());
            di.BindFactory(() => new AsyncCommand(_ => throw new InvalidOperationException("S05 primary")));
            CommandFailureContext? timeout = null;
            di.BindInstance<IRecoveryStrategy>(new Strategy(failure =>
            {
                if (failure.CommandType == typeof(AsyncCommand)) return RecoveryDecision.FallbackAsync<TimedFallback>();
                timeout = failure;
                return RecoveryDecision.Skip();
            }));
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            int failureSignals = 0;
            bus.Subscribe<CommandFailedSignal>(failure =>
            {
                if (failure.Exception is CommandTimeoutException) failureSignals++;
            });
            Task pending;
            if (route == 2)
            {
                bus.RegisterCommand(typeof(First), typeof(AsyncCommand), ExecutionMode.Sequential, 0, true);
                pending = bus.FireAsync(new First()).AsTask();
            }
            else
            {
                bus.RegisterCompositeCommand(new[] { typeof(First) },
                    route == 0 ? typeof(TimedComposite) : typeof(TimedFallback), false, 0, true);
                pending = bus.FireAsync(new First()).AsTask();
            }
            bool completed = false;
            try { completed = await Task.WhenAny(pending, Task.Delay(1500)) == pending; }
            finally
            {
                bus.Dispose();
                try { await pending; }
                catch (OperationCanceledException) { }
            }
            Require(completed && timeout.HasValue && timeout.Value.IsTimeout
                && timeout.Value.Exception is CommandTimeoutException && failureSignals == 1,
                $"S05: annotated async work must time out via recovery; route={route}, completed={completed}, isTimeout={timeout?.IsTimeout}, failedSignals={failureSignals}");
        }

        private readonly struct First { }
        private readonly struct Second { }

        [CommandTimeout(25)]
        private sealed class TimedComposite : IAsyncCompositeCommand
        {
            public ValueTask ExecuteAsync(CompositeContext context, CancellationToken ct)
                => new ValueTask(Task.Delay(Timeout.Infinite, ct));
        }

        [CommandTimeout(25)]
        private sealed class TimedFallback : IAsyncCommand
        {
            public ValueTask ExecuteAsync(CancellationToken ct)
                => new ValueTask(Task.Delay(Timeout.Infinite, ct));
        }

        private sealed class AsyncCommand : IAsyncCommand<First>
        {
            private readonly Func<CancellationToken, ValueTask> _run;
            public AsyncCommand(Func<CancellationToken, ValueTask> run) => _run = run;
            public ValueTask ExecuteAsync(First signal, CancellationToken ct) => _run(ct);
        }

        private sealed class SyncCommand : ICommand<First>
        {
            private readonly Action _run;
            public SyncCommand(Action run) => _run = run;
            public void Execute(First signal) => _run();
        }

        private sealed class Fallback : ICommand
        {
            private readonly Action _run;
            public Fallback(Action run) => _run = run;
            public void Execute() => _run();
        }

        private sealed class AsyncFallback : IAsyncCommand
        {
            private readonly Func<CancellationToken, ValueTask> _run;
            public AsyncFallback(Func<CancellationToken, ValueTask> run) => _run = run;
            public ValueTask ExecuteAsync(CancellationToken ct) => _run(ct);
        }

        private sealed class Composite : ICompositeCommand
        {
            private readonly Action _run;
            public Composite(Action run) => _run = run;
            public void Execute(CompositeContext context) => _run();
        }

        private sealed class Strategy : IRecoveryStrategy
        {
            private readonly Func<CommandFailureContext, RecoveryDecision> _decide;
            public Strategy(Func<CommandFailureContext, RecoveryDecision> decide) => _decide = decide;
            public RecoveryDecision OnCommandFailed(CommandFailureContext failure) => _decide(failure);
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

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
