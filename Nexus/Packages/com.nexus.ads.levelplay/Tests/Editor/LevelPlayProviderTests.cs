#if NEXUS_LEVELPLAY
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Ads;
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

        private sealed class UmpFlow : IConsentFlow
        {
            public bool Completed => true;
            public bool CanRequestAds => true;
            public bool PrivacyOptionsRequired => false;
            public event Action Changed;
            public Action Answer;
            public void Request(Action onCompleted) => Answer = onCompleted;
            public void ShowPrivacyOptions(Action onClosed) { }
            public void Raise() => Changed?.Invoke();
        }

        private static string UsNat(int sale)
        {
            // usnat v2 core, 24 bits (IAB offsets): Version 2 (0-5), notices 1,1,1,1,0,0 (6-17), SaleOptOut (18-19), SharingOptOut 2 (20-21), targeted 2 (22-23); four base64url characters
            long bits = (2L << 18) | (1L << 16) | (1L << 14) | (1L << 12) | (1L << 10) | ((long)sale << 4) | (2L << 2) | 2L;
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            return "" + alphabet[(int)((bits >> 18) & 63)] + alphabet[(int)((bits >> 12) & 63)] + alphabet[(int)((bits >> 6) & 63)] + alphabet[(int)(bits & 63)];
        }

        [Test]
        public void AUmpUsStateOptOut_ReachesLevelPlaysDoNotSell_AndAChangedAnswerIsSentAgain()
        {
            var sent = new List<bool>();
            var original = LevelPlayProvider.DoNotSellApi;
            LevelPlayProvider.DoNotSellApi = sent.Add;
            try
            {
                LevelPlayRegistration.Register();
                Assert.IsNotNull(AdsBootstrap.DoNotSellSink, "the registration hands the bootstrap LevelPlay's do_not_sell");
                var flow = new UmpFlow();
                var gpp = new Dictionary<string, string> { ["IABGPP_GppSID"] = "7", ["IABGPP_7_String"] = UsNat(sale: 2) };
                using (new UsPrivacySync(flow, k => gpp.TryGetValue(k, out var v) ? v : null, AdsBootstrap.DoNotSellSink))
                {
                    flow.Answer();
                    CollectionAssert.AreEqual(new[] { false }, sent, "UMP answered: no opt-out");
                    gpp["IABGPP_7_String"] = UsNat(sale: 1); // the player opens the privacy options and opts out
                    flow.Raise();
                    CollectionAssert.AreEqual(new[] { false, true }, sent);
                }
            }
            finally { LevelPlayProvider.DoNotSellApi = original; }
        }

        [Test]
        public void TheGameFlow_StillAppliesItsAnswerAsGdprAndDoNotSell()
        {
            var sent = new List<bool>();
            var original = LevelPlayProvider.DoNotSellApi;
            LevelPlayProvider.DoNotSellApi = sent.Add;
            try
            {
                // SetGdprConsent goes to the SDK's own call, which needs no init to store the flag
                LevelPlayProvider.ApplyConsent(false);
                LevelPlayProvider.ApplyConsent(true);
                CollectionAssert.AreEqual(new[] { true, false }, sent, "do_not_sell is the opposite of accepted");
            }
            finally { LevelPlayProvider.DoNotSellApi = original; }
        }
    }
}
#endif
