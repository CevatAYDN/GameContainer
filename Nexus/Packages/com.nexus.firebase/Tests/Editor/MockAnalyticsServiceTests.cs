using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Firebase.Mocks;

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

            analytics.LogEvent("ad_reward");
            analytics.LogEvent("run_start", new Dictionary<string, object> { { "error", "timeout" } });

            Assert.AreEqual(1, analytics.Events.Count, "Firebase drops ad_reward on the device, so the mock does not record it");
            Assert.AreEqual("timeout", analytics.Events[0].Parameters["error"], "error is reserved as an event name, not as a parameter name");
        }
    }
}