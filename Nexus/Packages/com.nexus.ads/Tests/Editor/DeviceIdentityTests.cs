using System;
using System.Threading;
using NUnit.Framework;
using Nexus.Ads.Unity;

namespace Nexus.Ads.Tests
{
    /// <summary>
    /// Android asks Play services for the advertising id on its own thread. When that lookup threw, the fallback read
    /// SystemInfo.deviceUniqueIdentifier there, which only the main thread may call: the read threw too, the callback never
    /// ran and the bootstrap never started ads in that session (issue #117).
    /// </summary>
    public sealed class DeviceIdentityTests
    {
        [Test]
        public void AFailedLookupOnTheLookupThreadAnswersWithTheFallbackId()
        {
            string answered = null;
            Exception escaped = null;
            var lookup = new Thread(() =>
            {
                try { answered = DeviceIdentity.ReadOrFallback(() => throw new InvalidOperationException("no Play services"), "device-id"); }
                catch (Exception ex) { escaped = ex; }
            });

            lookup.Start();

            Assert.That(lookup.Join(TimeSpan.FromSeconds(10)), Is.True, "the lookup thread did not finish");
            Assert.That(escaped, Is.Null, "an exception left the lookup thread, so the callback would never run");
            Assert.That(answered, Is.EqualTo("device-id"));
        }
    }
}
