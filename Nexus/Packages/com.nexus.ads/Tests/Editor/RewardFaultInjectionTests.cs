#if UNITY_EDITOR
using System;
using Nexus.Ads.Unity;
using NUnit.Framework;

namespace Nexus.Ads.Tests
{
    public sealed class RewardFaultInjectionTests
    {
        private sealed class Provider : IRewardFaultDebug, IDisposable
        {
            public RewardFaultInjection RewardFaults { get; } = new RewardFaultInjection("command-test");
            public void Dispose() => RewardFaults.Dispose();
        }

        [TestCase("delay:NaN")]
        [TestCase("delay:Infinity")]
        [TestCase("delay:-1")]
        [TestCase("delay:121")]
        [TestCase("delay:3,5")]
        [TestCase("invent")]
        public void IntentAndPanelCommandsRejectInvalidParameters(string command)
        {
            using var provider = new Provider();
            Assert.Throws<ArgumentException>(() => RewardFaultCommands.Execute(provider, command));
            Assert.That(provider.RewardFaults.DebugReplayLastReward(), Is.False);
        }

        [Test]
        public void IntentAndPanelCommandsShareTheProviderHook()
        {
            using var provider = new Provider();
            int deliveries = 0;
            RewardFaultCommands.Execute(provider, "next");
            var show = provider.RewardFaults.BeginShow("a", () => deliveries++, () => false);
            provider.RewardFaults.Receive(show);
            Assert.That(deliveries, Is.Zero);
            RewardFaultCommands.Execute(provider, "reset");
            Assert.That(deliveries, Is.EqualTo(1));
            RewardFaultCommands.Execute(provider, "delay:0.5");
        }

        private sealed class Scheduler : IAdScheduler, IDisposable
        {
            public Action Pending;
            public double Delay;
            public IDisposable Schedule(double seconds, Action action) { Delay = seconds; Pending = action; return this; }
            public void Run() { var action = Pending; Pending = null; action?.Invoke(); }
            public void Dispose() => Pending = null;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DelayUsesCloseBoundaryRegardlessOfCallbackOrder(bool rewardBeforeClose)
        {
            var scheduler = new Scheduler();
            using var faults = new RewardFaultInjection("test", scheduler);
            int deliveries = 0;
            faults.DebugDelayReward(TimeSpan.FromSeconds(3));
            var show = faults.BeginShow("a", () => deliveries++, () => true);
            if (rewardBeforeClose) faults.Receive(show);
            faults.Closed(show);
            if (!rewardBeforeClose) faults.Receive(show);
            Assert.That(deliveries, Is.Zero);
            Assert.That(scheduler.Delay, Is.EqualTo(3));
            scheduler.Run();
            faults.Receive(show);
            Assert.That(deliveries, Is.EqualTo(1));
            Assert.That(faults.DebugReplayLastReward(), Is.True);
            Assert.That(deliveries, Is.EqualTo(2), "Only explicit replay bypasses capture deduplication.");
        }

        [Test]
        public void NextShowWaitsForActualOpenAndResetPreservesReceivedReward()
        {
            using var faults = new RewardFaultInjection("test");
            int first = 0, second = 0;
            faults.DebugDeliverRewardDuringNextShow();
            var a = faults.BeginShow("a", () => first++, () => true);
            faults.Receive(a);
            faults.Opened(a);
            Assert.That(first, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => faults.DebugDelayReward(TimeSpan.Zero));
            var b = faults.BeginShow("b", () => second++, () => false);
            Assert.That(first, Is.Zero);
            faults.Opened(b);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.Zero);
            faults.DebugDeliverRewardDuringNextShow();
            var c = faults.BeginShow("c", () => first++, () => false);
            faults.Receive(c);
            faults.DebugReset();
            Assert.That(first, Is.EqualTo(2));
            Assert.That(faults.DebugReplayLastReward(), Is.False);
        }

        [Test]
        public void DisposeCancelsDeliveryAndReplayCannotInventAReward()
        {
            var scheduler = new Scheduler();
            using var faults = new RewardFaultInjection("test", scheduler);
            Assert.That(faults.DebugReplayLastReward(), Is.False);
            int deliveries = 0;
            faults.DebugDelayReward(TimeSpan.Zero);
            var show = faults.BeginShow("a", () => deliveries++, () => false);
            faults.Closed(show);
            Assert.That(scheduler.Pending, Is.Null);
            faults.Receive(show);
            faults.Dispose();
            scheduler.Run();
            faults.Receive(show);
            Assert.That(deliveries, Is.Zero);
            Assert.That(faults.DebugReplayLastReward(), Is.False);
        }
    }
}
#endif
