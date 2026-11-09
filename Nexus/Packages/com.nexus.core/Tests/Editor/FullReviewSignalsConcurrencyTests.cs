using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewSignalsConcurrencyTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public async Task S07_ConcurrentSiblings_RespectTheSharedDepthLimit()
        {
            using var di = new NexusDI();
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            using var bothAtIncrement = new CountdownEvent(2);
            using var startClaims = new ManualResetEventSlim();
            using var bothStarted = new CountdownEvent(2);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int entered = 0, rejected = 0, observed = -1;
            bus.SubscribeAsync<Waiting>((_, _) => new ValueTask(release.Task));
            bus.SubscribeAsync<Candidate>((_, _) =>
            {
                Interlocked.Increment(ref entered);
                return new ValueTask(release.Task);
            });
            bus.SubscribeAsync<Root>(async (_, _) =>
            {
                var waiting = new List<Task>();
                Task first = null, second = null;
                try
                {
                    // This root plus thirty suspended children occupy thirty-one slots.
                    for (int i = 0; i < 30; i++) waiting.Add(bus.FireAsync(new Waiting()).AsTask());
                    SignalBus.BeforeAsyncDepthIncrementForTests = type =>
                    {
                        if (type != typeof(Candidate)) return;
                        bothAtIncrement.Signal();
                        Require(startClaims.Wait(5000), "S07: claims were not released");
                    };
                    first = Task.Run(Race);
                    second = Task.Run(Race);
                    Require(bothAtIncrement.Wait(5000), "S07: both siblings must reach the forced boundary");
                    startClaims.Set();
                    Require(bothStarted.Wait(5000), "S07: both public calls must finish admission");
                    observed = Volatile.Read(ref entered);
                }
                finally
                {
                    startClaims.Set();
                    SignalBus.BeforeAsyncDepthIncrementForTests = null;
                    release.TrySetResult(true);
                    if (first != null) waiting.Add(first);
                    if (second != null) waiting.Add(second);
                    await Within(Task.WhenAll(waiting));
                }
                Require(observed == 1 && rejected == 1,
                    $"S07: only one sibling fits the remaining slot; entered={observed}, rejected={rejected}");

                // Every released child has unwound; the root must have its full child budget again.
                release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.Clear();
                try
                {
                    for (int i = 0; i < 31; i++) waiting.Add(bus.FireAsync(new Waiting()).AsTask());
                    Require(waiting.TrueForAll(task => !task.IsCompleted), "S07: completed children leaked depth claims");
                }
                finally
                {
                    release.TrySetResult(true);
                    await Within(Task.WhenAll(waiting));
                }
            });
            try { await Within(bus.FireAsync(new Root()).AsTask()); }
            finally { SignalBus.BeforeAsyncDepthIncrementForTests = null; }

            async Task Race()
            {
                var pending = bus.FireAsync(new Candidate()).AsTask();
                bothStarted.Signal();
                try { await pending; }
                catch (NexusReentrancyException) { Interlocked.Increment(ref rejected); }
            }
        }

        private readonly struct Root { }
        private readonly struct Waiting { }
        private readonly struct Candidate { }

        private static async Task Within(Task task)
        {
            Require(await Task.WhenAny(task, Task.Delay(10000)) == task, "S07: bounded dispatch did not finish");
            await task;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
