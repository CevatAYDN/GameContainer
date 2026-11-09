using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewSignalsOwnershipTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void S03_CancellationBeforeSyncConstruction_LeavesNoCallableSubscription()
        {
            CheckConstructionCancellation(false).GetAwaiter().GetResult();
        }

#if UNITY_EDITOR
        [Test]
#endif
        public Task S03_CancellationBeforeAsyncConstruction_LeavesNoCallableSubscription()
        {
            return CheckConstructionCancellation(true);
        }

        private static async Task CheckConstructionCancellation(bool asynchronous)
        {
            using var lifetime = new CancellationTokenSource();
            using var di = new NexusDI();
            using var bus = new SignalBus(di, new CommandPoolManager(di), new TokenContext(lifetime.Token));
            int calls = 0;
            ISignalSubscription subscription = null;
            SubscriptionRegistry.BeforeSubscriptionConstructionForTests = lifetime.Cancel;
            try
            {
                subscription = asynchronous
                    ? bus.SubscribeAsync<Signal>((_, _) => { calls++; return default; })
                    : bus.Subscribe<Signal>(_ => calls++);
            }
            finally { SubscriptionRegistry.BeforeSubscriptionConstructionForTests = null; }
            using (subscription)
            {
                if (asynchronous) await bus.FireAsync(new Signal(1));
                else bus.Fire(new Signal(1));
                Require(!subscription.IsActive && calls == 0,
                    $"S03: a cancelled subscription must not dispatch; async={asynchronous}, active={subscription.IsActive}, calls={calls}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void S08_CompletedComposite_ReleasesItsPayloadOwner() => CheckReleasedPayload(0);

#if UNITY_EDITOR
        [Test]
#endif
        public void S08_NestedComposite_ReleasesItsPayloadOwner() => CheckReleasedPayload(1);

#if UNITY_EDITOR
        [Test]
#endif
        public void S08_AbortedComposite_ReleasesItsPayloadOwner() => CheckReleasedPayload(2);

#if UNITY_EDITOR
        [Test]
#endif
        public void S08_CommandRetainedContext_RemainsReadableAfterDisposal()
        {
            using var di = new NexusDI();
            var pools = new CommandPoolManager(di);
            using var bus = new SignalBus(di, pools, new MockContext());
            CompositeContext retained = default;
            di.BindFactory(() => new CarryComposite(context => retained = context));
            bus.RegisterCompositeCommand(new[] { typeof(Payload) }, typeof(CarryComposite), false, 0, false);
            var owner = new object();
            bus.Fire(new Payload(owner));
            bus.Dispose();
            pools.Clear();
            Require(ReferenceEquals(owner, retained.Get<Payload>().Owner),
                "S08: an intentionally retained immutable context must keep its payload");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void S09_ClearDuringDrain_DefersNewWorkUntilTheFollowingDrain()
        {
            using var di = new NexusDI();
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            var queue = new HybridQueue(bus);
            var received = new List<int>();
            bus.Subscribe<Signal>(signal =>
            {
                received.Add(signal.Value);
                if (signal.Value != 1) return;
                queue.Clear();
                queue.EnqueueThreadSafe(new Signal(3));
            });
            queue.EnqueueThreadSafe(new Signal(1));
            queue.EnqueueThreadSafe(new Signal(2));
            queue.DrainThreadSafe();
            Require(string.Join(",", received) == "1" && queue.ThreadSafeQueueDepth == 1,
                $"S09: replacement work ran in the cleared drain; received={string.Join(",", received)}, pending={queue.ThreadSafeQueueDepth}");
            queue.DrainThreadSafe();
            Require(string.Join(",", received) == "1,3" && queue.ThreadSafeQueueDepth == 0
                && queue.TotalEnqueued == 3 && queue.TotalDrained == 2,
                "S09: the second drain must deliver the replacement once, never the cleared item");
        }

#if UNITY_EDITOR && !NEXUS_DEBUG
        [Test]
#endif
        public void S09_ClearAndDrain_AfterWarmup_DoesNotAllocate()
        {
            using var di = new NexusDI();
            using var bus = new SignalBus(di, new CommandPoolManager(di), new MockContext());
            var queue = new HybridQueue(bus);
            int total = 0;
            bus.Subscribe<Signal>(signal =>
            {
                total += signal.Value;
                if (signal.Value != 1) return;
                queue.Clear();
                queue.EnqueueThreadSafe(new Signal(3));
            });
            for (int i = 0; i < 256; i++) Cycle();
            int initial = total;
#if UNITY_EDITOR
            using var probe = new GcAllocationProbe();
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            for (int i = 0; i < 1000; i++) Cycle();
#if UNITY_EDITOR
            long allocated = probe.Stop();
#else
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            Require(allocated == 0 && total == initial + 4000,
                $"S09: warmed clear/drain allocated {allocated} bytes/samples; delivered={total - initial}");

            void Cycle()
            {
                queue.EnqueueThreadSafe(new Signal(1));
                queue.EnqueueThreadSafe(new Signal(2));
                queue.DrainThreadSafe();
                queue.DrainThreadSafe();
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void S10_ClearRacingTraceRegistration_DoesNotRetainTheSink()
        {
            var control = UnretainedOwner();
            var sink = CompleteTraceRegistrationRace();
            try
            {
                Collect();
                Require(!control.IsAlive, "S10: collection control remained reachable");
                Require(!sink.IsAlive, "S10: the cleared plugin left its sink rooted in global tracing");
            }
            finally { RemoveTraceSinkForCleanup(sink); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CompleteTraceRegistrationRace()
        {
            var context = new PluginContext(new TracePlugin(), null);
            var sink = new TraceSink();
            var weak = new WeakReference(sink);
            using var publishReached = new ManualResetEventSlim();
            using var publishRelease = new ManualResetEventSlim();
            using var clearStarted = new ManualResetEventSlim();
            object publicationLock = null;
            Exception registrationFailure = null, clearFailure = null;
            PluginContext.BeforeTraceSinkPublishForTests = gate =>
            {
                publicationLock = gate;
                publishReached.Set();
                Require(publishRelease.Wait(5000), "S10: publication gate was not released");
            };
            var registering = new Thread(() =>
            {
                try { context.RegisterTraceSink(sink); }
                catch (Exception ex) { registrationFailure = ex; }
            }) { IsBackground = true };
            var clearing = new Thread(() =>
            {
                clearStarted.Set();
                try { context.Clear(); }
                catch (Exception ex) { clearFailure = ex; }
            }) { IsBackground = true };
            bool clearWasStarted = false;
            try
            {
                registering.Start();
                Require(publishReached.Wait(5000), "S10: registration never reached publication");
                // Observe only the scheduling gate, before the clearing thread starts.
                // Unserialized publication lets Clear finish first; atomic publication
                // keeps Clear behind it. The assertions below concern public ownership.
                bool publicationOwnsLock = !Monitor.TryEnter(publicationLock);
                if (!publicationOwnsLock) Monitor.Exit(publicationLock);
                clearing.Start();
                clearWasStarted = true;
                Require(clearStarted.Wait(5000), "S10: clearing did not start");
                if (!publicationOwnsLock)
                    Require(clearing.Join(5000), "S10: Clear must finish in the exposed publication gap");
                publishRelease.Set();
                Require(registering.Join(5000) && clearing.Join(5000), "S10: registration/clear did not finish");
                Require(registrationFailure == null && clearFailure == null && context.TraceSinks.Count == 0,
                    $"S10: cleanup left local ownership or failed; owned={context.TraceSinks.Count}, register={registrationFailure}, clear={clearFailure}");
            }
            finally
            {
                publishRelease.Set();
                registering.Join(5000);
                if (clearWasStarted) clearing.Join(5000);
                PluginContext.BeforeTraceSinkPublishForTests = null;
                context.Clear();
            }
            return weak;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveTraceSinkForCleanup(WeakReference weak)
        {
            if (weak.Target is INexusTraceSink sink) NexusTrace.RemoveSink(sink);
        }

        private sealed class TracePlugin : INexusPlugin
        {
            public NexusPluginManifest Manifest { get; } = new("S10", "1", PluginCapabilities.TraceProvider);
            public void OnPluginRegistered(IPluginContext context) { }
            public void OnPluginRemoved() { }
        }

        private sealed class TraceSink : INexusTraceSink
        {
            public void Write(in TraceEvent traceEvent) { }
        }

        private static void CheckReleasedPayload(int mode)
        {
            var control = UnretainedOwner();
            var owner = CompleteComposite(mode);
            Collect();
            Require(!control.IsAlive, "S08: collection control remained reachable");
            Require(!owner.IsAlive, $"S08: completed dispatch retained its payload owner; mode={mode}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CompleteComposite(int mode)
        {
            using var di = new NexusDI();
            var pools = new CommandPoolManager(di);
            using var bus = new SignalBus(di, pools, new MockContext());
            int calls = 0;
            di.BindFactory(() => new CarryComposite(context =>
            {
                calls++;
                if (mode == 1) bus.Fire(new NestedPayload(context.Get<Payload>().Owner));
                if (mode == 2) throw new NexusReentrancyException("S08 abort probe");
            }));
            di.BindFactory(() => new NestedComposite());
            bus.RegisterCompositeCommand(new[] { typeof(Payload) }, typeof(CarryComposite), false, 0, false);
            bus.RegisterCompositeCommand(new[] { typeof(NestedPayload) }, typeof(NestedComposite), false, 0, false);
            var owner = new object();
            var weak = new WeakReference(owner);
            try { bus.Fire(new Payload(owner)); }
            catch (NexusReentrancyException) when (mode == 2) { }
            bus.Dispose();
            pools.Clear();
            Require(calls == 1, "S08: the composite did not execute");
            return weak;
        }

        // Create the control before the operation, as in SaveThrottlerRetentionTests,
        // so its factory frame is not the last operation before conservative stack scanning.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference UnretainedOwner() => new WeakReference(new object());

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private readonly struct Payload
        {
            public readonly object Owner;
            public Payload(object owner) => Owner = owner;
        }

        private readonly struct NestedPayload
        {
            public readonly object Owner;
            public NestedPayload(object owner) => Owner = owner;
        }

        private sealed class CarryComposite : ICompositeCommand
        {
            private readonly Action<CompositeContext> _run;
            public CarryComposite(Action<CompositeContext> run) => _run = run;
            public void Execute(CompositeContext context) => _run(context);
        }

        private sealed class NestedComposite : ICompositeCommand
        {
            public void Execute(CompositeContext context)
                => Require(context.Get<NestedPayload>().Owner != null, "S08: nested dispatch lost its payload");
        }

        private sealed class TokenContext : MockContext, IContext
        {
            private readonly CancellationToken _token;
            public TokenContext(CancellationToken token) => _token = token;
            CancellationToken IContext.LifetimeToken => _token;
        }

        private readonly struct Signal
        {
            public readonly int Value;
            public Signal(int value) => Value = value;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
