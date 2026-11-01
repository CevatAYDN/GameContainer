using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The signal and command defects the audit #206 found: a cancelled or aborted Concurrent dispatch wrote each
    /// handler's error again (#213), a throwing CommandFailedSignal subscriber escaped FireAsync (#214), a sync fallback
    /// made later async fallbacks share one depth (#215), a failed async composite never ran its FallbackAsync (#216), a
    /// retried composite under FireAsync kept its state (#217), an unsubscribed handler stayed reachable during a
    /// dispatch (#218), NexusRuntime.Reset removed the binder's dispatchers (#219), HasHandlers missed a composite
    /// (#220), the Tracer tab's Clear detached every sink (#221) and FireAsync was not counted (#223).
    /// </summary>
    public sealed class SignalAuditDefectTests
    {
        public sealed class Strategy : IRecoveryStrategy
        {
            public Func<CommandFailureContext, RecoveryDecision> Decide = _ => RecoveryDecision.Skip();
            public RecoveryDecision OnCommandFailed(CommandFailureContext failure) => Decide(failure);
        }

        public struct HangSignal { }
        public sealed class Hang1 : IAsyncCommand<HangSignal> { public async ValueTask ExecuteAsync(HangSignal s, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct); }
        public sealed class Hang2 : IAsyncCommand<HangSignal> { public async ValueTask ExecuteAsync(HangSignal s, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct); }

        public struct ConcurrentSignal { }
        public sealed class ThrowingConcurrent : IAsyncCommand<ConcurrentSignal> { public async ValueTask ExecuteAsync(ConcurrentSignal s, CancellationToken ct) { await Task.Yield(); throw new InvalidOperationException("concurrent failure"); } }
        public sealed class QuietConcurrent : IAsyncCommand<ConcurrentSignal> { public async ValueTask ExecuteAsync(ConcurrentSignal s, CancellationToken ct) => await Task.Yield(); }

        public struct ObservedSignal { }
        public sealed class ThrowingObserved : IAsyncCommand<ObservedSignal> { public async ValueTask ExecuteAsync(ObservedSignal s, CancellationToken ct) { await Task.Yield(); throw new InvalidOperationException("observed failure"); } }

        public struct SyncFailSignal { }
        public struct AsyncFailSignal { }
        public sealed class FailingSync : ICommand<SyncFailSignal> { public void Execute(SyncFailSignal s) => throw new InvalidOperationException("sync failure"); }
        public sealed class FailingAsync : IAsyncCommand<AsyncFailSignal> { public ValueTask ExecuteAsync(AsyncFailSignal s, CancellationToken ct) => throw new InvalidOperationException("async failure"); }
        public sealed class SyncFallback : ICommand { public void Execute() { } }
        public sealed class GatedFallback : IAsyncCommand
        {
            public static int Started;
            public static TaskCompletionSource<bool> Gate;
            public async ValueTask ExecuteAsync(CancellationToken ct) { Interlocked.Increment(ref Started); await Gate.Task; }
        }

        public struct PartA { }
        public struct PartB { }
        public sealed class FailingAsyncComposite : IAsyncCompositeCommand { public ValueTask ExecuteAsync(CompositeContext s, CancellationToken ct) => throw new InvalidOperationException("composite failure"); }
        public sealed class AsyncFallback : IAsyncCommand { public static int Runs; public ValueTask ExecuteAsync(CancellationToken ct) { Runs++; return default; } }

        public struct RetryA { }
        public struct RetryB { }
        public sealed class RetriedComposite : ICompositeCommand, IResettableCommand
        {
            public static readonly List<int> Seen = new();
            public static int Attempts;
            private int _calls;
            public void ResetState() => _calls = 0;
            public void Execute(CompositeContext s)
            {
                Seen.Add(++_calls);
                if (++Attempts == 1) throw new InvalidOperationException("first attempt");
            }
        }

        public struct TickSignal { }

        public sealed class GeneratedCommand { }
        public struct GeneratedSignal { }

        public struct WaitedP { }
        public struct WaitedQ { }
        public sealed class WaitingComposite : ICompositeCommand { public void Execute(CompositeContext s) { } }

        public sealed class CountingSink : INexusTraceSink
        {
            public int Writes;
            public void Write(in TraceEvent traceEvent) => Interlocked.Increment(ref Writes);
        }

        public struct CountedSignal { }

        // Error lines from any thread: an async command's continuation can write on a pool thread.
        private sealed class ErrorLines : IDisposable
        {
            public readonly ConcurrentQueue<string> Lines = new();

            public ErrorLines()
            {
                LogAssert.ignoreFailingMessages = true;
                Application.logMessageReceivedThreaded += OnLog;
            }

            private void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Lines.Enqueue(message);
            }

            public int Count(string needle) => Lines.Count(l => l.Contains(needle));
            public override string ToString() => string.Join("\n---\n", Lines);

            public void Dispose()
            {
                Application.logMessageReceivedThreaded -= OnLog;
                LogAssert.ignoreFailingMessages = false;
            }
        }

        private readonly List<IDisposable> _owned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }

        private SignalBus NewBus(IRecoveryStrategy strategy = null, params Type[] commands)
        {
            var di = new NexusDI();
            _owned.Add(di);
            foreach (var c in commands) di.Bind(c, c, isSingleton: false);
            if (strategy != null) di.BindInstance(strategy);
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            _owned.Add(bus);
            return bus;
        }

        [Test]
        public async Task DisposingTheBusDuringTwoConcurrentAsyncCommands_WritesNoErrorLine()
        {
            using var errors = new ErrorLines();
            var bus = NewBus(null, typeof(Hang1), typeof(Hang2));
            bus.RegisterCommand(typeof(HangSignal), typeof(Hang1), ExecutionMode.Concurrent, 0, true);
            bus.RegisterCommand(typeof(HangSignal), typeof(Hang2), ExecutionMode.Concurrent, 1, true);

            var pending = bus.FireAsync(new HangSignal()).AsTask();
            bus.Dispose();
            try { await pending; }
            catch (OperationCanceledException) { }

            Assert.IsEmpty(errors.Lines, errors.ToString());
        }

        [Test]
        public async Task AnAbortDecisionOnAConcurrentFailure_WritesTheCommandsErrorOnce()
        {
            using var errors = new ErrorLines();
            var bus = NewBus(new Strategy { Decide = _ => RecoveryDecision.Abort() }, typeof(ThrowingConcurrent), typeof(QuietConcurrent));
            bus.RegisterCommand(typeof(ConcurrentSignal), typeof(ThrowingConcurrent), ExecutionMode.Concurrent, 0, true);
            bus.RegisterCommand(typeof(ConcurrentSignal), typeof(QuietConcurrent), ExecutionMode.Concurrent, 1, true);

            try
            {
                await bus.FireAsync(new ConcurrentSignal());
                Assert.Fail("an Abort decision ends the dispatch with NexusRecoveryAbortException");
            }
            catch (NexusRecoveryAbortException) { }

            Assert.AreEqual(1, errors.Lines.Count, errors.ToString());
            StringAssert.Contains($"Command {nameof(ThrowingConcurrent)} failed", errors.Lines.Single());
        }

        [Test]
        public async Task AThrowingCommandFailedSignalSubscriber_StaysInsideFireAsync()
        {
            using var errors = new ErrorLines();
            var bus = NewBus(null, typeof(ThrowingObserved));
            bus.RegisterCommand(typeof(ObservedSignal), typeof(ThrowingObserved), ExecutionMode.Sequential, 0, true);
            bus.Subscribe<CommandFailedSignal>(_ => throw new InvalidOperationException("observer of the async failure"));
            bool heard = false;
            bus.Subscribe<ObservedSignal>(_ => heard = true);

            await bus.FireAsync(new ObservedSignal());

            Assert.IsTrue(heard, "the signal's own subscriber still runs");
            Assert.AreEqual(1, errors.Count("observer of the async failure"), errors.ToString());
        }

        [Test]
        public async Task ASyncFallbackOnTheThread_LeavesLaterConcurrentAsyncFallbacksTheirOwnDepth()
        {
            using var errors = new ErrorLines();
            GatedFallback.Started = 0;
            GatedFallback.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var strategy = new Strategy
            {
                Decide = f => f.CommandType == typeof(FailingSync) ? RecoveryDecision.Fallback<SyncFallback>() : RecoveryDecision.FallbackAsync<GatedFallback>()
            };
            var bus = NewBus(strategy, typeof(FailingSync), typeof(FailingAsync), typeof(SyncFallback), typeof(GatedFallback));
            bus.RegisterCommand(typeof(SyncFailSignal), typeof(FailingSync), ExecutionMode.Sequential, 0, false);
            bus.RegisterCommand(typeof(AsyncFailSignal), typeof(FailingAsync), ExecutionMode.Sequential, 0, true);

            bus.Fire(new SyncFailSignal());
            var pending = Enumerable.Range(0, 4).Select(_ => bus.FireAsync(new AsyncFailSignal()).AsTask()).ToArray();
            int started = GatedFallback.Started;
            GatedFallback.Gate.TrySetResult(true);
            await Task.WhenAll(pending);

            Assert.AreEqual(4, started, errors.ToString());
        }

        [Test]
        public async Task AFailedAsyncComposite_RunsItsFallbackAsync()
        {
            using var errors = new ErrorLines();
            AsyncFallback.Runs = 0;
            var bus = NewBus(new Strategy { Decide = _ => RecoveryDecision.FallbackAsync<AsyncFallback>() }, typeof(FailingAsyncComposite), typeof(AsyncFallback));
            bus.RegisterCompositeCommand(new[] { typeof(PartA), typeof(PartB) }, typeof(FailingAsyncComposite), false, 0, true);

            await bus.FireAsync(new PartA());
            await bus.FireAsync(new PartB());

            Assert.AreEqual(1, AsyncFallback.Runs, errors.ToString());
        }

        [Test]
        public async Task ARetriedCompositeUnderFireAsync_StartsEachAttemptFromAResetInstance()
        {
            using var errors = new ErrorLines();
            RetriedComposite.Seen.Clear();
            RetriedComposite.Attempts = 0;
            var bus = NewBus(new Strategy { Decide = _ => RecoveryDecision.Retry(3) }, typeof(RetriedComposite));
            bus.RegisterCompositeCommand(new[] { typeof(RetryA), typeof(RetryB) }, typeof(RetriedComposite), false, 0, false);

            await bus.FireAsync(new RetryA());
            await bus.FireAsync(new RetryB());

            CollectionAssert.AreEqual(new[] { 1, 1 }, RetriedComposite.Seen, errors.ToString());
        }

        [Test]
        public void AHandlerUnsubscribedDuringADispatch_IsNoLongerHeldByTheRegistry()
        {
            using var registry = new SubscriptionRegistry { ImmediateSweepWhenIdle = true };
            registry.EnterDispatch();
            try
            {
                var payload = new object();
                var subscription = registry.Subscribe<TickSignal>(_ => GC.KeepAlive(payload), CancellationToken.None);
                subscription.Dispose();

                // the sweep waits for the dispatch; until then the node is still in the chain
                var node = registry.SubscriptionsReadCopy[typeof(TickSignal)];
                Assert.IsNull(node.Handler);
                Assert.IsNull(node.RawSubscription);
                Assert.IsFalse(registry.HasActiveSubscriptions(typeof(TickSignal)));
            }
            finally { registry.ExitDispatch(); }
        }

        [Test]
        public void TheBindersGenericDispatchers_SurviveNexusRuntimeReset()
        {
            Action<object, object> syncDispatcher = (command, signal) => { };
            Func<object, object, CancellationToken, ValueTask> asyncDispatcher = (command, signal, ct) => default;
            CommandRegistry.RegisterGenericSyncDispatcher(typeof(GeneratedCommand), typeof(GeneratedSignal), syncDispatcher);
            CommandRegistry.RegisterGenericAsyncDispatcher(typeof(GeneratedCommand), typeof(GeneratedSignal), asyncDispatcher);

            NexusRuntime.Reset();

            using var di = new NexusDI();
            var registry = new CommandRegistry(di);
            Assert.AreSame(syncDispatcher, registry.GetGenericSyncDispatcher(typeof(GeneratedCommand), typeof(GeneratedSignal)));
            Assert.AreSame(asyncDispatcher, registry.GetGenericAsyncDispatcher(typeof(GeneratedCommand), typeof(GeneratedSignal)));
        }

        [Test]
        public void HasHandlers_SeesACompositeRegisteredWithRegisterCompositeCommand()
        {
            var bus = NewBus(null, typeof(WaitingComposite));
            bus.RegisterCompositeCommand(new[] { typeof(WaitedP), typeof(WaitedQ) }, typeof(WaitingComposite), false, 0, false);

            Assert.IsTrue(bus.HasHandlers(typeof(WaitedP)));
        }

#if NEXUS_DEBUG
        [Test]
        public void TheTracerTabsClear_KeepsTheGamesTraceSinks()
        {
            var sink = new CountingSink();
            NexusTrace.AddSink(sink);
            try
            {
                typeof(TracerPlugin).GetMethod("ClearTraces", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(new TracerPlugin(), null);

                NexusTrace.EndEvent(NexusTrace.BeginEvent(TraceEventType.Signal, "TracerClearProbe"));

                Assert.Greater(sink.Writes, 0);
            }
            finally { NexusTrace.RemoveSink(sink); }
        }
#endif

        [Test]
        public async Task FireAsync_IsCountedInTotalSignalsDispatched()
        {
            bool enabled = NexusRuntime.Metrics.MetricsEnabled;
            NexusRuntime.Metrics.MetricsEnabled = true;
            try
            {
                var bus = NewBus();
                bus.Subscribe<CountedSignal>(_ => { });
                long before = NexusRuntime.Metrics.TotalSignalsDispatched;

                await bus.FireAsync(new CountedSignal());

                Assert.AreEqual(1, NexusRuntime.Metrics.TotalSignalsDispatched - before);
            }
            finally { NexusRuntime.Metrics.MetricsEnabled = enabled; }
        }
    }
}
