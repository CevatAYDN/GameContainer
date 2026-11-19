#if NEXUS_ADS_FIREBASE
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Firebase.Analytics;

namespace Nexus.Ads.Bridge.Tests
{
    /// <summary>Issue #430: Firebase consent mode follows the game-drawn consent screen's stored answer.</summary>
    public sealed class GameConsentAnalyticsConsentTests
    {
        private sealed class MemoryStore : IPacingStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
            public string Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => _values[key] = value;
        }

        [Test]
        public void TheReader_FollowsTheStoredAnswer_AcceptGrantsRejectDenies_AndAnUnansweredQuestionHolds()
        {
            var answers = new ConsentAnswerStore(new MemoryStore(), 1);
            var consent = new GameConsentAnalyticsConsent(() => answers);
            Assert.AreEqual(ConsentState.Required, consent.State, "asked, not answered");
            Assert.AreEqual(AnalyticsDecision.Hold, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice));

            answers.Write(true);
            Assert.AreEqual(ConsentState.Obtained, consent.State);
            Assert.AreEqual(AnalyticsDecision.Send, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice));

            answers.Write(false);
            Assert.AreEqual(AnalyticsDecision.Discard, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice));
        }

        [Test]
        public void ANewQuestionVersion_HoldsAnalyticsAgainUntilItIsAnswered()
        {
            var store = new MemoryStore();
            new ConsentAnswerStore(store, 1).Write(true);
            var consent = new GameConsentAnalyticsConsent(() => new ConsentAnswerStore(store, 2));
            Assert.AreEqual(AnalyticsDecision.Hold, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice));
        }

        [Test]
        public void WithoutARegisteredFlow_NothingIsSent()
        {
            var consent = new GameConsentAnalyticsConsent(() => null);
            Assert.AreEqual(ConsentState.Unknown, consent.State);
            Assert.AreEqual(AnalyticsDecision.Hold, AnalyticsCollectionPolicy.Decide(consent.State, consent.Choice));
        }
    }
}
#endif
