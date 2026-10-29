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
        // Event names an app cannot log, as Google publishes them (read 2026-10-29); the two lists differ and GA4's "isn't exhaustive".
        // GA4 event naming rules, Mobile (Android and iOS): https://support.google.com/analytics/answer/13316687
        private static readonly string[] Ga4ReservedAppEventNames =
        {
            "ad_activeview", "ad_click", "ad_exposure", "ad_query", "ad_reward", "adunit_exposure", "app_clear_data", "app_exception",
            "app_install", "app_remove", "app_store_refund", "app_update", "app_upgrade", "dynamic_link_app_open", "dynamic_link_app_update",
            "dynamic_link_first_open", "error", "firebase_campaign", "firebase_in_app_message_action", "firebase_in_app_message_dismiss",
            "firebase_in_app_message_impression", "first_open", "first_visit", "notification_dismiss", "notification_foreground",
            "notification_open", "notification_receive", "notification_send", "os_update", "session_start", "user_engagement",
        };

        // The Android SDK, "events with these names will be dropped":
        // https://firebase.google.com/docs/reference/android/com/google/firebase/analytics/FirebaseAnalytics.Event
        private static readonly string[] SdkDroppedEventNames =
        {
            "ad_activeview", "ad_click", "ad_exposure", "ad_query", "ad_reward", "adunit_exposure", "app_background", "app_clear_data",
            "app_exception", "app_remove", "app_store_refund", "app_store_subscription_cancel", "app_store_subscription_convert",
            "app_store_subscription_renew", "app_update", "app_upgrade", "dynamic_link_app_open", "dynamic_link_app_update",
            "dynamic_link_first_open", "error", "first_open", "first_visit", "notification_dismiss", "notification_foreground",
            "notification_open", "notification_receive", "os_update", "session_start", "session_start_with_rollout", "user_engagement",
        };

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

        [TestCaseSource(nameof(Ga4ReservedAppEventNames))]
        public void AnalyticsEventName_RefusesEveryNameOnGoogleAnalyticsReservedAppList(string name)
        {
            Assert.IsFalse(AnalyticsEventName.IsValid(name), name);
        }

        [TestCaseSource(nameof(SdkDroppedEventNames))]
        public void AnalyticsEventName_RefusesEveryNameTheAndroidSdkDrops(string name)
        {
            Assert.IsFalse(AnalyticsEventName.IsValid(name), name);
        }

        [TestCase("error", true)]
        [TestCase("session_start", true)]
        [TestCase("ga_session", false)]
        [TestCase("run start", false)]
        public void AnalyticsParameterName_KeepsTheShapeAndPrefixRulesButNotTheReservedEventNames(string name, bool valid)
        {
            Assert.AreEqual(valid, AnalyticsEventName.IsValidParameterName(name), name);
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