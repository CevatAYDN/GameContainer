#if NEXUS_ADS_FIREBASE
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Nexus.Firebase.Mocks;

namespace Nexus.Ads.Bridge.Tests
{
    /// <summary>The ad family's remote configuration over com.nexus.firebase's remote values (issue #198).</summary>
    public sealed class FirebaseAdsRemoteConfigTests
    {
        // A remote value is text in the invariant culture: a phone set to Turkish, whose decimal separator is a comma,
        // cannot read "90.5" with its own culture.
        [Test]
        public void PacingValues_AreReadInTheInvariantCulture_AndAnAbsentOrUnreadableValueIsNotReported()
        {
            var remote = new MockRemoteConfigService();
            var config = new FirebaseAdsRemoteConfig(remote);
            remote.Set(AdPolicy.Keys.MinInterstitialGapSeconds, "90.5");
            remote.Set(AdPolicy.Keys.MaxInterstitialsPerDay, "many");
            var culture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
            try
            {
                Assert.That(config.TryGetDouble(AdPolicy.Keys.MinInterstitialGapSeconds, out double gap) ? gap : -1, Is.EqualTo(90.5));
                Assert.That(config.TryGetDouble(AdPolicy.Keys.MaxInterstitialsPerDay, out _), Is.False, "an unreadable value");
                Assert.That(config.TryGetDouble(AdPolicy.Keys.SessionResumeGapMinutes, out _), Is.False, "an absent value");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        // The ad controller reads the kill switch again on every change; an absent key must leave ads on.
        [Test]
        public void TheKillSwitch_IsOffUntilRemoteTurnsItOn_AndTheChangeIsAnnounced()
        {
            var remote = new MockRemoteConfigService();
            var config = new FirebaseAdsRemoteConfig(remote);
            int changes = 0;
            config.Changed += () => changes++;
            Assert.IsFalse(config.KillSwitch);

            remote.Set(FirebaseAdsRemoteConfig.KillSwitchKey, "true");

            Assert.That((config.KillSwitch, changes), Is.EqualTo((true, 1)));
        }
    }
}
#endif
