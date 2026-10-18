using NUnit.Framework;
using Nexus.Firebase.Mocks;

namespace Nexus.Firebase.Tests
{
    /// <summary>The editor and development mock must refuse what the SDK refuses, or a test passes that a player's device would not record.</summary>
    public sealed class MockAnalyticsServiceTests
    {
        [Test]
        public void MockAnalytics_DropsAnEventNameFirebaseWouldRefuse()
        {
            var analytics = new MockAnalyticsService();

            analytics.LogEvent("Run Start");
            analytics.LogEvent("run_start");

            Assert.AreEqual(1, analytics.Events.Count);
            Assert.AreEqual("run_start", analytics.Events[0].Name);
        }
    }
}
