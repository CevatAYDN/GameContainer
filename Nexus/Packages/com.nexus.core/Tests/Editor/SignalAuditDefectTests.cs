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
    /// handler's error again (#213).
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
    }
}
