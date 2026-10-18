using System;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Nexus.Firebase.Mocks;
using Nexus.Firebase.RemoteConfig;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// The fetch cache and the value parsing of docs/ANALYTICS.md, Remote config. A value the game cannot read must fall back to the game's
    /// default; a device language must not change how a number reads.
    /// </summary>
    public sealed class RemoteConfigTests
    {
        private static readonly DateTime Launch = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

        [Test]
        public void FetchPolicy_FirstLaunchAlwaysFetches()
        {
            Assert.IsTrue(RemoteConfigFetchPolicy.IsFetchDue(null, Launch));
        }

        [Test]
        public void FetchPolicy_LaunchInsideTheCacheLifetimeMakesNoRequest()
        {
            Assert.IsFalse(RemoteConfigFetchPolicy.IsFetchDue(Launch, Launch.AddHours(11).AddMinutes(59)));
        }

        [Test]
        public void FetchPolicy_CacheOlderThanTheLifetimeIsFetchedAgain()
        {
            Assert.IsTrue(RemoteConfigFetchPolicy.IsFetchDue(Launch, Launch + RemoteConfigFetchPolicy.CacheLifetime));
        }

        [Test]
        public void FetchPolicy_ClockSetBackForcesAFetch()
        {
            Assert.IsTrue(RemoteConfigFetchPolicy.IsFetchDue(Launch, Launch.AddMinutes(-5)));
        }

        [TestCase("abc", 3)]
        [TestCase("", 3)]
        [TestCase("1.5", 3)]
        public void Parser_IntegerThatCannotBeReadFallsBack(string raw, int fallback)
        {
            Assert.AreEqual(fallback, RemoteConfigParser.ToInt(raw, fallback));
        }

        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("")]
        public void Parser_DoubleThatIsNotAFiniteNumberFallsBack(string raw)
        {
            Assert.AreEqual(1.0, RemoteConfigParser.ToDouble(raw, 1.0));
        }

        [Test]
        public void Parser_DoubleReadsWithADotWhateverTheDeviceCulture()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                Assert.AreEqual(0.5, RemoteConfigParser.ToDouble("0.5", 1.0), 1e-12);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestCase("true", true)]
        [TestCase("TRUE", true)]
        [TestCase("1", true)]
        [TestCase("false", false)]
        [TestCase("0", false)]
        public void Parser_BooleanAcceptsTheConsoleSpellings(string raw, bool expected)
        {
            Assert.AreEqual(expected, RemoteConfigParser.ToBool(raw, !expected));
        }

        [Test]
        public void MockRemoteConfig_UnsetKeyReturnsTheGameFallback()
        {
            var config = new MockRemoteConfigService();
            Assert.AreEqual(12, config.GetInt("ads_double_wax_daily_cap", 12));
            Assert.IsFalse(config.GetBool("ads_interstitial_enabled", false));
        }

        [Test]
        public void MockRemoteConfig_ChangedFiresOncePerRealChange()
        {
            var config = new MockRemoteConfigService();
            int raised = 0;
            config.Changed += _ => raised++;

            config.Set("economy_wax_multiplier", "1.25");
            config.Set("economy_wax_multiplier", "1.25");
            config.Set("economy_wax_multiplier", "1.5");

            Assert.AreEqual(2, raised, "the identical second set raised nothing");
            Assert.AreEqual(1.5, config.GetDouble("economy_wax_multiplier", 1.0), 1e-12);
        }
    }
}
