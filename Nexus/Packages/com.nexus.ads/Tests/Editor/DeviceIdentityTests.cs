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
        // Issue #439: iOS handed the vendor id to the Test tab under the name "advertising id".
        [Test]
        public void OnlyAndroidsIdIsCalledAnAdvertisingId_TheVendorIdSaysItIsNotTheIdfa()
        {
            Assert.That(DeviceIdentity.KindFor(isEditor: false, isAndroid: true), Is.EqualTo(DeviceIdKind.AndroidAdvertisingId));
            Assert.That(DeviceIdentity.KindFor(isEditor: false, isAndroid: false), Is.EqualTo(DeviceIdKind.VendorId));
            Assert.That(DeviceIdentity.KindFor(isEditor: true, isAndroid: true), Is.EqualTo(DeviceIdKind.Editor));
            StringAssert.Contains("advertising id", DeviceIdentity.Label(DeviceIdKind.AndroidAdvertisingId));
            StringAssert.Contains("not the IDFA", DeviceIdentity.Label(DeviceIdKind.VendorId));
            StringAssert.DoesNotContain("advertising id", DeviceIdentity.Label(DeviceIdKind.VendorId));
        }

        [Test]
        public void AFailedLookupOnTheLookupThreadAnswersWithTheFallbackId()
        {
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus Ads\] the advertising id is unavailable \(InvalidOperationException: no Play services\)")); // #198
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
