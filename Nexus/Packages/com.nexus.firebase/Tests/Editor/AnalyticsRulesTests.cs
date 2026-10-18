using NUnit.Framework;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// The name limits Firebase documents and the consent decision. A name Firebase refuses is dropped without a word; an event logged while
    /// consent was unknown must not reach the SDK before the answer.
    /// </summary>
    public sealed class AnalyticsRulesTests
    {
        [TestCase("run_start")]
        [TestCase("Run_Start")]
        [TestCase("ad_result2")]
        [TestCase("a")]
        public void AnalyticsEventName_AcceptsLettersDigitsAndUnderscoresInAnyCase(string name)
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

        [TestCase("2nd_run")]
        [TestCase("_run")]
        [TestCase("run-start")]
        [TestCase("run start")]
        [TestCase("run\n")]
        [TestCase("firebase_start")]
        [TestCase("GA_start")]
        [TestCase("google_play")]
        [TestCase("")]
        [TestCase(null)]
        public void AnalyticsEventName_RefusesWhatFirebaseRefuses(string name)
        {
            Assert.IsFalse(AnalyticsEventName.IsValid(name));
        }

        [TestCase("level_up", true)]
        [TestCase("user_level_twenty_four_chars", false)]
        [TestCase("user_id", false)]
        [TestCase("first_open_time", false)]
        [TestCase("ga_flag", false)]
        public void AnalyticsUserProperty_RefusesReservedAndLongNames(string name, bool valid)
        {
            Assert.AreEqual(valid, AnalyticsUserProperty.IsValidName(name), name);
        }

        [Test]
        public void AnalyticsUserProperty_ValueIsLimitedToThirtySixCharacters()
        {
            Assert.IsTrue(AnalyticsUserProperty.IsValidValue(new string('v', AnalyticsUserProperty.MaxValueLength)));
            Assert.IsFalse(AnalyticsUserProperty.IsValidValue(new string('v', AnalyticsUserProperty.MaxValueLength + 1)));
            Assert.IsFalse(AnalyticsUserProperty.IsValidValue(null), "a null value would remove the property");
        }

        [TestCase(ConsentState.Unknown, AnalyticsChoice.Unknown, AnalyticsDecision.Hold)]
        [TestCase(ConsentState.Required, AnalyticsChoice.Unknown, AnalyticsDecision.Hold)]
        [TestCase(ConsentState.NotRequired, AnalyticsChoice.Unknown, AnalyticsDecision.Send)]
        [TestCase(ConsentState.Obtained, AnalyticsChoice.Unknown, AnalyticsDecision.Hold)]
        [TestCase(ConsentState.Obtained, AnalyticsChoice.Granted, AnalyticsDecision.Send)]
        [TestCase(ConsentState.Obtained, AnalyticsChoice.Denied, AnalyticsDecision.Discard)]
        public void AnalyticsDecision_FollowsTheAnswerAndTheChoice(ConsentState state, AnalyticsChoice choice, AnalyticsDecision expected)
        {
            Assert.AreEqual(expected, AnalyticsCollectionPolicy.Decide(state, choice));
        }
    }
}