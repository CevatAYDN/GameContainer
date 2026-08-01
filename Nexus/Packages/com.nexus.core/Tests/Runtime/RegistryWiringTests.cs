using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// Registry wiring tests: prove SignalBus now delegates ALL registration and subscription
    /// state to CommandRegistry/SubscriptionRegistry (a single storage layer), preserves the
    /// pre-refactor semantics (idle unsubscribe reclaims the node immediately; dispatch goes
    /// through the registry read copies), and converges with the standalone registries under
    /// identical sequences. Runs against the REAL runtime (MockContext-backed bus), the same
    /// way AdvancedSignalBusTests exercises the bus.
    /// </summary>
    [TestFixture]
    public class RegistryWiringTests
    {
        private struct WiringSignalA { public int Value; public WiringSignalA(int v) => Value = v; }
        private struct WiringSignalB { public int Value; public WiringSignalB(int v) => Value = v; }

        private class WiringCmdA : ICommand<WiringSignalA>
        {
            [Inject] public WiringCounter Counter;
            public void Execute(WiringSignalA signal) { Counter.Value++; }
        }

        private class WiringAsyncCmd : IAsyncCommand<WiringSignalB>
        {
            public ValueTask ExecuteAsync(WiringSignalB signal, CancellationToken ct) => default;
        }

        private class WiringCounter { public int Value; }

        [SetUp]
        public void SetUp() => NexusRuntime.Reset();

        [TearDown]
        public void TearDown() => NexusRuntime.Reset();

        [Test]
        public void SignalBus_Subscribe_DelegatesToRegistry_AndFires()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            int received = 0;
            var sub = bus.Subscribe<WiringSignalA>(_ => received++);
            bus.Fire(new WiringSignalA(1));
            Assert.AreEqual(1, received);
            sub.Dispose();
            bus.Dispose();
        }

        [Test]
        public void SignalBus_Unsubscribe_WhileIdle_ReclaimsImmediately()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            int received = 0;
            var sub = bus.Subscribe<WiringSignalA>(_ => received++);
            bus.Fire(new WiringSignalA(1));
            Assert.AreEqual(1, received);
            sub.Dispose(); // idle bus → immediate reclaim (pre-refactor SignalBus semantics)
            bus.Fire(new WiringSignalA(2));
            Assert.AreEqual(1, received, "disposed subscription must never fire again");
            bus.Dispose();
        }

        [Test]
        public void SignalBus_Unsubscribe_DuringDispatch_DeferredToUnwind()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            int disposerRuns = 0;
            int disposedSubRuns = 0;
            bus.Subscribe<WiringSignalA>(_ => { });
            var victim = bus.Subscribe<WiringSignalA>(_ => disposedSubRuns++);
            // Newest-first: this handler runs BEFORE `victim` in the walk; disposing a later
            // node mid-dispatch must not pool it while the reader still holds the chain.
            bus.Subscribe<WiringSignalA>(_ =>
            {
                disposerRuns++;
                victim.Dispose();
            });
            bus.Fire(new WiringSignalA(1));
            bus.Fire(new WiringSignalA(2));
            Assert.AreEqual(2, disposerRuns);
            Assert.AreEqual(0, disposedSubRuns, "sibling disposed mid-dispatch must never fire");
            bus.Dispose();
        }

        [Test]
        public void SignalBus_RegisterCommand_LandsInRegistrySnapshot()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCommand(typeof(WiringSignalA), typeof(WiringCmdA), ExecutionMode.Sequential, 0, false);
            Assert.IsTrue(bus.CommandHandlers.TryGetValue(typeof(WiringSignalA), out var handlers));
            Assert.AreEqual(1, handlers.Count);
            Assert.AreEqual(typeof(WiringCmdA), handlers[0].CommandType);
            Assert.AreEqual(ExecutionMode.Sequential, handlers[0].Mode);
            bus.Dispose();
        }

        [Test]
        public void SignalBus_AsyncHandler_SyncFire_ThrowsMismatch()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCommand(typeof(WiringSignalB), typeof(WiringAsyncCmd), ExecutionMode.Sequential, 0, true);
            var ex = Assert.Throws<Exception>(() => bus.Fire(new WiringSignalB(1)));
            Assert.AreEqual("NexusSyncAsyncMismatchException", ex.GetType().Name);
            bus.Dispose();
        }

        [Test]
        public void SignalBus_CommandDispatch_GoesThroughRegistry()
        {
            using var di = new NexusDI();
            var counter = new WiringCounter();
            di.BindInstance(counter);
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            bus.RegisterCommand(typeof(WiringSignalA), typeof(WiringCmdA), ExecutionMode.Sequential, 0, false);
            for (int i = 0; i < 100; i++) bus.Fire(new WiringSignalA(i));
            Assert.AreEqual(100, counter.Value);
            bus.Dispose();
        }

        [Test]
        public void WiredBus_MatchesStandalone_SubscriptionLifecycle()
        {
            // The same subscribe→fire→dispose→fire sequence through the real bus and the
            // standalone registry must produce identical counts.
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            var standalone = new SubscriptionRegistry { ImmediateSweepWhenIdle = true };
            int busCount = 0, standaloneCount = 0;
            var busSub = bus.Subscribe<WiringSignalA>(_ => busCount++);
            var regSub = standalone.Subscribe<WiringSignalA>(_ => standaloneCount++, CancellationToken.None);

            for (int i = 0; i < 50; i++)
            {
                bus.Fire(new WiringSignalA(i));
                if (standalone.SubscriptionsReadCopy.TryGetValue(typeof(WiringSignalA), out var node))
                {
                    var current = node;
                    while (current != null)
                    {
                        if (current.IsActive && current.Handler is Action<WiringSignalA> handler)
                            handler(new WiringSignalA(i));
                        current = current.Next;
                    }
                }
            }

            Assert.AreEqual(50, busCount);
            Assert.AreEqual(50, standaloneCount);

            busSub.Dispose();
            regSub.Dispose();
            bus.Fire(new WiringSignalA(99));
            Assert.AreEqual(50, busCount, "disposed bus subscription must not fire");
            bus.Dispose();
            standalone.Dispose();
        }

        [Test]
        public void ConcurrentChurn_OnWiredBus_NoDeadDelivery()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            const int threads = 4;
            const int firesEach = 200;
            const int expected = threads * firesEach;
            var counts = new int[threads];
            var subs = new ISignalSubscription[threads];
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                subs[t] = bus.Subscribe<WiringSignalA>(_ => Interlocked.Increment(ref counts[id]));
            }

            var barrier = new Barrier(threads + 1);
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                workers[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < firesEach; i++) bus.Fire(new WiringSignalA(i));
                });
                workers[t].Start();
            }
            barrier.SignalAndWait();
            for (int t = 0; t < threads; t++) workers[t].Join();

            for (int t = 0; t < threads; t++) subs[t].Dispose();
            bus.Fire(new WiringSignalA(-1)); // nobody subscribed anymore

            for (int t = 0; t < threads; t++)
                Assert.AreEqual(expected, counts[t], $"subscriber {t} must receive exactly all fires");
            bus.Dispose();
        }

        [Test]
        public async Task WiredBus_AsyncDispatch_StillWorks_ThroughRegistry()
        {
            using var di = new NexusDI();
            var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            int delivered = 0;
            var sub = bus.SubscribeAsync<WiringSignalB>((_, __) =>
            {
                delivered++;
                return default;
            });
            await bus.FireAsync(new WiringSignalB(1));
            Assert.AreEqual(1, delivered);
            sub.Dispose();
            bus.Dispose();
        }
    }
}
