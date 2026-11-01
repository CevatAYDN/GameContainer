using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Firebase.Mocks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Firebase.Tests
{
    /// <summary>The editor and development mock must refuse what the SDK refuses, or a test passes that a player's device would not record.</summary>
    public sealed class MockAnalyticsServiceTests
    {
        [Test]
        public void MockAnalytics_DropsEventsAndParametersFirebaseWouldRefuse()
        {
            var analytics = new MockAnalyticsService();
            var parameters = new Dictionary<string, object> { { "night", 3 }, { "Bad Name", 1 } };
            LogAssert.Expect(LogType.Warning, new Regex(@"event 'Run Start' is not sent: the name holds ' '"));
            LogAssert.Expect(LogType.Warning, new Regex(@"parameter 'Bad Name' of event 'run_start' is not sent: the name holds ' '"));

            analytics.LogEvent("Run Start");
            analytics.LogEvent("run_start", parameters);
            parameters["night"] = 4;

            Assert.AreEqual(1, analytics.Events.Count);
            Assert.AreEqual("run_start", analytics.Events[0].Name);
            Assert.AreEqual(1, analytics.Events[0].Parameters.Count, "the refused parameter is dropped");
            Assert.AreEqual(3, analytics.Events[0].Parameters["night"], "the event keeps the value it was logged with");
        }

        [Test]
        public void MockAnalytics_DropsAReservedEventNameButKeepsAParameterNamedLikeOne()
        {
            var analytics = new MockAnalyticsService();
            LogAssert.Expect(LogType.Warning, new Regex(@"event 'ad_reward' is not sent: the name is one Google reserves for events"));

            analytics.LogEvent("ad_reward");
            analytics.LogEvent("run_start", new Dictionary<string, object> { { "error", "timeout" } });

            Assert.AreEqual(1, analytics.Events.Count, "Firebase drops ad_reward on the device, so the mock does not record it");
            Assert.AreEqual("timeout", analytics.Events[0].Parameters["error"], "error is reserved as an event name, not as a parameter name");
        }

        // Issue #123: a name Firebase refuses was dropped without a word, on the device and in Unity, so a game learned of the loss only
        // from the Firebase console. The mock and the Firebase service share the report.
        [Test]
        public void ARefusedName_IsReportedOncePerName_WithTheRuleItBroke()
        {
            var analytics = new MockAnalyticsService();
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics event 'firebase_start' is not sent: the name starts with the reserved prefix firebase_"));
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics event '' is not sent: the name is empty"));
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics event '1st_run' is not sent: the name does not start with a letter"));
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics parameter 'when' of event 'run_start' is not sent: Firebase has no type for its value \(System.DateTime\)"));
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics user property 'user_id' is not set: the name is one Google reserves for user properties"));
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Firebase\] Analytics user property 'tier' is not set: the value is longer than 36 characters"));

            for (int i = 0; i < 2; i++)
            {
                analytics.LogEvent("firebase_start");
                analytics.LogEvent("");
                analytics.LogEvent("1st_run");
                analytics.LogEvent("run_start", new Dictionary<string, object> { { "when", System.DateTime.UtcNow } });
                analytics.SetUserProperty("user_id", "42");
                analytics.SetUserProperty("tier", new string('x', 37));
            }

            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(2, analytics.Events.Count, "only the valid event was recorded, twice");
            Assert.IsFalse(analytics.Events[0].Parameters.ContainsKey("when"), "the device drops a value Firebase has no type for, so the mock does too");
            Assert.AreEqual(0, analytics.UserProperties.Count);
        }
    }
}