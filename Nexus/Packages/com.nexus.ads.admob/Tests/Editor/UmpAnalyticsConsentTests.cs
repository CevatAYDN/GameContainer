using GoogleMobileAds.Ump.Api;
using NUnit.Framework;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

namespace Nexus.Ads.AdMob.Tests
{
    /// <summary>
    /// The framework's analytics consent over Google's consent form (issue #87, rule (b)): the form answers nothing about
    /// analytics, so where consent is required nothing is sent; Wickling read TCF purpose 1 as the analytics choice and sent.
    /// </summary>
    public sealed class UmpAnalyticsConsentTests
    {
        [TestCase(ConsentStatus.NotRequired, AnalyticsDecision.Send)]
        [TestCase(ConsentStatus.Unknown, AnalyticsDecision.Hold)]
        [TestCase(ConsentStatus.Required, AnalyticsDecision.Hold)]
        [TestCase(ConsentStatus.Obtained, AnalyticsDecision.Discard)]
        public void AnAnsweredConsentForm_SendsNothing_WhereConsentIsRequired(ConsentStatus status, AnalyticsDecision expected)
        {
            using var ads = new AdService();
            var consent = new UmpAnalyticsConsent(() => status) { Ads = ads };
            consent.InitializeAsync(default);

            Assert.That(AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice), Is.EqualTo(expected));
        }

        [Test]
        public void TheAnswer_IsReadAgainOnReadyChanged_AndReportedOnlyWhenItChanges()
        {
            var status = ConsentStatus.Unknown;
            using var ads = new AdService();
            var consent = new UmpAnalyticsConsent(() => status) { Ads = ads };
            int changes = 0;
            consent.Changed += () => changes++;
            consent.InitializeAsync(default);

            ads.SetNetworkAdapter(null);
            int unchanged = changes;
            status = ConsentStatus.Obtained;
            ads.SetNetworkAdapter(null);
            int afterAnswer = changes;
            consent.OnDispose();
            status = ConsentStatus.NotRequired;
            ads.SetNetworkAdapter(null);

            Assert.That((unchanged, afterAnswer, consent.State, changes), Is.EqualTo((1, 2, ConsentState.Obtained, 2)));
        }
    }
}
