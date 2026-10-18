using NUnit.Framework;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// The event-name rule and the consent rule of docs/ANALYTICS.md. A name Firebase refuses is dropped by the SDK without a word, and analytics
    /// collected before consent break the consent promise.
    /// </summary>
    public sealed class AnalyticsRulesTests
    {
        [TestCase("run_start")]
        [TestCase("level_up")]
        [TestCase("ad_result2")]
        [TestCase("a")]
        public void AnalyticsEventName_AcceptsSnakeCaseNames(string name)
        {
            Assert.IsTrue(AnalyticsEventName.IsValid(name), name);
        }

        [Test]
        public void AnalyticsEventName_AcceptsNamesUpToFortyCharacters()
        {
            string longest = new string('a', AnalyticsEventName.MaxLength);
            Assert.IsTrue(AnalyticsEventName.IsValid(longest));
            Assert.IsFalse(AnalyticsEventName.IsValid(longest + "a"), "forty-one characters are refused");
        }

        [TestCase("Run_Start")]
        [TestCase("2nd_run")]
        [TestCase("run-start")]
        [TestCase("run start")]
        [TestCase("_run")]
        [TestCase("run\n")]
        [TestCase("")]
        [TestCase(null)]
        public void AnalyticsEventName_RefusesNamesFirebaseWouldDrop(string name)
        {
            Assert.IsFalse(AnalyticsEventName.IsValid(name));
        }

        [TestCase(ConsentState.Unknown, false)]
        [TestCase(ConsentState.Required, false)]
        [TestCase(ConsentState.NotRequired, true)]
        [TestCase(ConsentState.Obtained, true)]
        public void AnalyticsCollection_StaysOffUntilConsentAllowsIt(ConsentState state, bool allowed)
        {
            Assert.AreEqual(allowed, AnalyticsCollectionPolicy.IsCollectionAllowed(state));
        }
    }
}
