using System;
using System.Collections.Generic;
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
        [TearDown]
        public void TearDown() => UmpConsentFlow.SetDebugDevices(null);

        // Issue #121: the reader asked UMP for nothing, so a first session stayed Unknown (events held) until the ads started.
        [Test]
        public void AFirstSession_AsksUmpForTheStatusAtStart_AndSendsOnceItAnswersNotRequired()
        {
            var status = ConsentStatus.Unknown;
            var request = new ConsentRequestParameters();
            ConsentRequestParameters asked = null;
            Action<FormError> answer = null;
            using var ads = new AdService();
            var consent = new UmpAnalyticsConsent(() => status, () => request, (sent, callback) => { asked = sent; answer = callback; }) { Ads = ads };
            consent.InitializeAsync(default);
            AnalyticsDecision beforeTheAnswer = AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice);

            status = ConsentStatus.NotRequired;
            answer?.Invoke(null);

            Assert.That(asked, Is.SameAs(request));
            Assert.That((beforeTheAnswer, consent.State, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice)),
                Is.EqualTo((AnalyticsDecision.Hold, ConsentState.NotRequired, AnalyticsDecision.Send)));
        }

        // The launch update and the consent flow send one request: a second request with another age tag or debug
        // geography would replace the first answer (issue #121). A release build never carries the debug devices.
        [Test]
        public void TheConsentRequest_CarriesTheAgeTag_AndTheDebugDevicesOnlyInATestConfiguration()
        {
            UmpConsentFlow.SetDebugDevices(new[] { "HASHED-ID" }, eeaGeography: true);

            ConsentRequestParameters test = UmpConsentFlow.RequestParameters(Config(testMode: true, underAgeOfConsent: true));
            ConsentRequestParameters release = UmpConsentFlow.RequestParameters(Config(testMode: false, underAgeOfConsent: false));

            Assert.That((test.TagForUnderAgeOfConsent, test.ConsentDebugSettings.DebugGeography, string.Join(",", test.ConsentDebugSettings.TestDeviceHashedIds)),
                Is.EqualTo((true, DebugGeography.EEA, "HASHED-ID")));
            Assert.That((release.TagForUnderAgeOfConsent, release.ConsentDebugSettings.DebugGeography, release.ConsentDebugSettings.TestDeviceHashedIds.Count),
                Is.EqualTo((false, DebugGeography.Disabled, 0)));
        }

        private static ProviderConfig Config(bool testMode, bool underAgeOfConsent) => new ProviderConfig(
            "app-key", new Dictionary<AdFormat, string>(), testMode, null, false, false, underAgeOfConsent, "PG");

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
