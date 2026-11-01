using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nexus.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #189: a subscriber that threw kept the older subscribers, the composites and, across contexts, the source's own
    /// commands and the other target contexts from the signal; an ObservableProperty observer that threw stopped the others
    /// and lost a value set during the notification; the testing kit delivered oldest first while the bus delivers newest first.
    /// </summary>
    public sealed class SignalIsolationTests
    {
        private struct IsoA { }
        private struct IsoB { }
        private struct IsoAsync { }
        private struct IsoOuter { }
        private struct IsoInner { }
        [CrossContext] public struct IsoBroadcast { }

        public sealed class IsoCountingComposite : ICompositeCommand
        {
            public static int Runs;
            public void Execute(CompositeContext signals) => Runs++;
        }

        public sealed class IsoFailingComposite : ICompositeCommand
        {
            public void Execute(CompositeContext signals) => throw new InvalidOperationException("composite bug");
        }

        public sealed class IsoBroadcastCommand : ICommand<IsoBroadcast>
        {
            public static int Runs;
            public void Execute(IsoBroadcast signal) => Runs++;
        }

        [SetUp]
        public void ResetCounters()
        {
            IsoCountingComposite.Runs = 0;
            IsoBroadcastCommand.Runs = 0;
        }

        private static Regex Isolated(string source, string message) =>
            new Regex(@"^\[Nexus\] " + Regex.Escape(source) + @" threw: System\.InvalidOperationException: " + message);

        [Test]
        public void ASubscriberThatThrows_LeavesTheOlderSubscribersAndTheComposites_ThenFireRethrowsIt()
        {
            using var test = NexusTestHarness.CreateContext();
            var bus = (SignalBus)test.Context.SignalBus;
            bus.RegisterCompositeCommand(new[] { typeof(IsoA), typeof(IsoB) }, typeof(IsoCountingComposite), false, 0, false);
            int older = 0;
            bus.Subscribe<IsoB>(_ => { });
            bus.Subscribe<IsoA>(_ => older++);
            bus.Subscribe<IsoA>(_ => throw new InvalidOperationException("newest subscriber bug"));
            bus.Fire(new IsoB());
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoA", "newest subscriber bug"));

            var thrown = Assert.Throws<InvalidOperationException>(() => bus.Fire(new IsoA()));

            Assert.AreEqual("newest subscriber bug", thrown.Message);
            Assert.AreEqual(1, older, "the older subscriber heard the signal");
            Assert.AreEqual(1, IsoCountingComposite.Runs, "the composite ran");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ACompositeThatThrows_LeavesTheOtherComposites()
        {
            using var test = NexusTestHarness.CreateContext();
            var bus = (SignalBus)test.Context.SignalBus;
            bus.RegisterCompositeCommand(new[] { typeof(IsoA), typeof(IsoB) }, typeof(IsoFailingComposite), true, 10, false);
            bus.RegisterCompositeCommand(new[] { typeof(IsoA), typeof(IsoB) }, typeof(IsoCountingComposite), false, 0, false);
            bus.Subscribe<IsoA>(_ => { });
            bus.Subscribe<IsoB>(_ => { });
            bus.Fire(new IsoB());
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] Command IsoFailingComposite failed: composite bug"));
            // The failed command fires CommandFailedSignal, which this test does not handle.
            LogAssert.Expect(LogType.Warning, new Regex(@"^\[Nexus\] Signal 'Nexus\.Core\.CommandFailedSignal' fired but has no subscribers"));

            Assert.Throws<InvalidOperationException>(() => bus.Fire(new IsoA()));

            Assert.AreEqual(1, IsoCountingComposite.Runs, "the lower-priority composite ran");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnAsyncSubscriberThatThrows_LeavesTheOlderSubscribers()
        {
            using var test = NexusTestHarness.CreateContext();
            var bus = test.Context.SignalBus;
            int older = 0;
            bus.SubscribeAsync<IsoAsync>((_, ct) => { older++; return default; });
            bus.SubscribeAsync<IsoAsync>((_, ct) => throw new InvalidOperationException("async subscriber bug"));
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoAsync", "async subscriber bug"));

            var fire = bus.FireAsync(new IsoAsync()).AsTask();

            Assert.IsTrue(fire.IsFaulted, "every subscriber completes synchronously, so the dispatch has ended");
            Assert.IsInstanceOf<InvalidOperationException>(fire.Exception.InnerException);
            Assert.AreEqual(1, older, "the older subscriber heard the signal");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AFailureInANestedFire_IsWrittenOnce()
        {
            using var test = NexusTestHarness.CreateContext();
            var bus = test.Context.SignalBus;
            bus.Subscribe<IsoInner>(_ => throw new InvalidOperationException("inner subscriber bug"));
            bus.Subscribe<IsoOuter>(_ => bus.Fire(new IsoInner()));
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoInner", "inner subscriber bug"));

            Assert.Throws<InvalidOperationException>(() => bus.Fire(new IsoOuter()));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ATargetContextWhoseSubscriberThrows_LeavesTheSourcesCommandAndTheOtherTargets()
        {
            // The configured source context has no lifecycle; it only binds the command.
            LogAssert.Expect(LogType.Warning, "[Nexus] No IContextLifecycle was discovered or registered. The context can still run, but setup may be incomplete.");
            using var source = NexusTestHarness.CreateContext(builder => builder.BindCommand<IsoBroadcast, IsoBroadcastCommand>());
            using var first = new Context();
            using var second = new Context();
            int heard = 0;
            // Each target holds a listener and, newer, a subscriber that throws: whichever target runs first throws.
            foreach (var target in new[] { first, second })
            {
                target.SignalBus.Subscribe<IsoBroadcast>(_ => heard++);
                target.SignalBus.Subscribe<IsoBroadcast>(_ => throw new InvalidOperationException("target bug"));
            }
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoBroadcast", "target bug"));
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoBroadcast", "target bug"));

            Assert.Throws<InvalidOperationException>(() => source.Context.SignalBus.Fire(new IsoBroadcast()));

            Assert.AreEqual(1, IsoBroadcastCommand.Runs, "the source's own command ran");
            Assert.AreEqual(2, heard, "both target contexts heard the signal");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnObserverThatThrows_LeavesTheOthers_AndAValueSetDuringTheNotificationIsDelivered()
        {
            var property = new ObservableProperty<int>(0);
            var seen = new List<string>();
            bool thrown = false;
            property.OnChanged((old, now) => { if (now == 1) property.Value = 5; });
            property.OnChanged((old, now) => { if (!thrown) { thrown = true; throw new InvalidOperationException("observer bug"); } });
            property.OnChanged((old, now) => seen.Add(old + "->" + now));
            LogAssert.Expect(LogType.Error, Isolated("An observer of ObservableProperty<Int32>", "observer bug"));

            Assert.Throws<InvalidOperationException>(() => property.Value = 1);

            Assert.AreEqual(5, property.Value);
            CollectionAssert.AreEqual(new[] { "0->1", "1->5" }, seen);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TheTestingKit_DeliversInTheBusOrder_AndIsolatesAThrowingSubscriberLikeTheBus()
        {
            using var test = NexusTestHarness.CreateContext();
            var recording = new RecordingSignalBus();
            var real = new List<string>();
            var kit = new List<string>();
            var buses = new (ISignalBus bus, List<string> heard)[] { (test.Context.SignalBus, real), (recording, kit) };
            foreach (var (bus, heard) in buses)
            {
                bus.Subscribe<IsoA>(_ => heard.Add("older"));
                bus.Subscribe<IsoA>(_ => { heard.Add("newer"); throw new InvalidOperationException("kit parity"); });
            }
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoA", "kit parity"));
            LogAssert.Expect(LogType.Error, Isolated("A subscriber of IsoA", "kit parity"));

            Assert.Throws<InvalidOperationException>(() => test.Context.SignalBus.Fire(new IsoA()));
            Assert.Throws<InvalidOperationException>(() => recording.Fire(new IsoA()));

            CollectionAssert.AreEqual(new[] { "newer", "older" }, real, "the bus delivers newest first");
            CollectionAssert.AreEqual(real, kit);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
