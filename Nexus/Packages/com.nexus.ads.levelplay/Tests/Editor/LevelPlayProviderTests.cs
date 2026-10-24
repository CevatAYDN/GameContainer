#if NEXUS_LEVELPLAY
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Ads.Unity;

namespace Nexus.Ads.LevelPlay.Tests
{
    internal sealed class NeverScheduler : IAdScheduler
    {
        private sealed class Handle : IDisposable { public void Dispose() { } }
        public int Scheduled;
        public IDisposable Schedule(double seconds, Action action) { Scheduled++; return new Handle(); }
    }

    /// <summary>The provider before the SDK answered: nothing is ready, nothing shows, the consent flow is a pass-through. The editor SDK itself needs Play Mode (its mock prefabs use DontDestroyOnLoad); see the PlayMode tests.</summary>
    public sealed class LevelPlayProviderTests
    {
        [Test]
        public void BeforeInitialization_NothingIsReady_AndAShowAnswersNotReady()
        {
            var provider = new LevelPlayProvider(new NeverScheduler());
            Assert.IsFalse(provider.IsReady(AdFormat.Rewarded));
            Assert.IsFalse(provider.IsReady(AdFormat.Interstitial));
            AdResult? result = null;
            provider.Show(AdFormat.Rewarded, "shop_refill", r => result = r);
            Assert.AreEqual(AdResult.NotReady, result);
        }

        [Test]
        public void LoadBeforeInitialization_ReportsLoadFailed_WithTheReason()
        {
            var provider = new LevelPlayProvider(new NeverScheduler());
            string reason = null;
            provider.LoadFailed += (_, r) => reason = r;
            provider.Load(AdFormat.Rewarded);
            StringAssert.Contains("not initialized", reason);
        }

        [Test]
        public void ConsentFlow_IsAPassThrough_BecauseLevelPlayShipsNoForm()
        {
            var provider = new LevelPlayProvider(new NeverScheduler());
            Assert.IsTrue(provider.Completed);
            Assert.IsTrue(provider.CanRequestAds);
            Assert.IsFalse(provider.PrivacyOptionsRequired);
            bool completed = false, closed = false;
            provider.Request(() => completed = true);
            provider.ShowPrivacyOptions(() => closed = true);
            Assert.IsTrue(completed && closed);
        }
    }
}
#endif
