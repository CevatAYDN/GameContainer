using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #74: games kept their own recording signal bus, in-memory preferences and fake ad adapter because the package
    /// shipped none and the mock adapter answered only NotReady or Rewarded.
    /// </summary>
    public sealed class TestingKitTests
    {
        private struct Scored { public int Points; }
        private struct Unfired { }

        [Test]
        public void RecordingSignalBusRecordsInOrderAndADisposedSubscriptionHearsNothing()
        {
            var bus = new RecordingSignalBus();
            var heard = new List<int>();
            var subscription = bus.Subscribe<Scored>(s => heard.Add(s.Points));

            bus.Fire(new Scored { Points = 1 });
            bus.Fire(new Scored { Points = 2 });
            subscription.Dispose();
            bus.Fire(new Scored { Points = 3 });

            Assert.That(heard, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(bus.Count<Scored>(), Is.EqualTo(3));
            Assert.That(bus.Last<Scored>().Points, Is.EqualTo(3));
            Assert.Throws<InvalidOperationException>(() => bus.Last<Unfired>());
        }

        [Test]
        public async Task RecordingSignalBusAwaitsAsyncSubscribersAndRefusesThemOnSyncFireAsTheRealBusDoes()
        {
            var bus = new RecordingSignalBus();
            int handled = 0;
            bus.SubscribeAsync<Scored>(async (s, ct) => { await Task.Yield(); handled += s.Points; });

            await bus.FireAsync(new Scored { Points = 5 });

            Assert.That(handled, Is.EqualTo(5));
            Assert.Throws<NexusSyncAsyncMismatchException>(() => bus.Fire(new Scored { Points = 1 }));
        }

        [Test]
        public void InMemoryPrefsAnswersEveryCrossTypeReadAsUnityPlayerPrefsDoes()
        {
            var device = new UnityPlayerPrefsService();
            var memory = new InMemoryPlayerPrefsService();
            var keys = new[] { "NexusKitParity_bool", "NexusKitParity_long", "NexusKitParity_int", "NexusKitParity_float" };
            try
            {
                foreach (IPlayerPrefsService prefs in new IPlayerPrefsService[] { device, memory })
                {
                    prefs.SetBool(keys[0], true);
                    prefs.SetLong(keys[1], -9_000_000_000L);
                    prefs.SetInt(keys[2], 7);
                    prefs.SetFloat(keys[3], 1.5f);
                }

                foreach (var key in keys)
                {
                    Assert.That(memory.GetInt(key, -1), Is.EqualTo(device.GetInt(key, -1)), key);
                    Assert.That(memory.GetBool(key), Is.EqualTo(device.GetBool(key)), key);
                    Assert.That(memory.GetString(key, "none"), Is.EqualTo(device.GetString(key, "none")), key);
                    Assert.That(memory.GetFloat(key, -1f), Is.EqualTo(device.GetFloat(key, -1f)), key);
                    Assert.That(memory.GetLong(key, -1), Is.EqualTo(device.GetLong(key, -1)), key);
                }
            }
            finally
            {
                foreach (var key in keys) device.DeleteKey(key);
            }
        }

        [Test]
        public async Task MockAdapterReportsTheSelectedRewardOutcome()
        {
            using var ads = new AdService();
            await ads.InitializeAsync(default);
            var mock = new MockAdNetworkAdapter { RewardOutcome = AdOutcome.Closed };
            ads.SetNetworkAdapter(mock);

            AdOutcome? outcome = null;
            bool? paid = null;
            ads.RequestReward("level_end", o => outcome = o);
            ads.ShowRewarded("level_end", p => paid = p);
            Assert.That(outcome, Is.EqualTo(AdOutcome.Closed));
            Assert.That(paid, Is.False);

            mock.RewardOutcome = AdOutcome.Failed;
            ads.RequestReward("level_end", o => outcome = o);
            Assert.That(outcome, Is.EqualTo(AdOutcome.Failed));
        }
    }
}
