using System;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// A release build disables the logger's Log and LogWarning. Warnings from an SDK boundary (an ad
    /// that did not load, a store that is not reachable) must still reach the player log; a game on
    /// Google Play saw no [UnityIAP] line in logcat while its product was missing.
    /// </summary>
    public sealed class ReleaseLoggingTests
    {
        private sealed class RecordingLogger : ILoggerService
        {
            public string LastWarning;
            public bool IsEnabled { get; set; } = true;
            public void Log(string message) { }
            public void LogWarning(string message) => LastWarning = message;
            public void LogError(string message) { }
            public void LogException(Exception exception) { }
        }

        [Test]
        public void WarnAlways_ReachesTheUnityLog_WhenTheLoggerIsDisabled()
        {
            var logger = new LoggerService { IsEnabled = false };
            LogAssert.Expect(LogType.Warning, "[UnityIAP] Products not fetched");

            logger.LogWarning("[UnityIAP] dropped like any release warning");
            NexusLog.WarnAlways(logger, "[UnityIAP] Products not fetched");

            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// Framework errors go through NexusRuntime.Logger. With no ILoggerService bound it returned null and
        /// every `Logger?.LogError` dropped the error: a ChipCrisis device build stopped at a black Boot screen
        /// with no log line. Without a registered logger the error must still reach Unity's log.
        /// </summary>
        [Test]
        public void FrameworkError_ReachesTheUnityLog_WhenNoLoggerIsRegistered()
        {
            NexusRuntime.Reset();
            LogAssert.Expect(LogType.Error, "[Nexus] Root initialization failed: test");

            NexusRuntime.Logger?.LogError("[Nexus] Root initialization failed: test");

            LogAssert.NoUnexpectedReceived();
        }

        // Issue #164: SaveThrottler said "the failure is always logged — never silent", through LogWarning, which a release player drops
        [Test]
        public void ASaveThatFails_IsWarnedInAReleasePlayerToo()
        {
            NexusRuntime.Reset(); // the fallback logger, which a release player disables; a logger another test left cached may be enabled
            NexusBuild.OverrideForTests(false);
            try
            {
                Assert.IsFalse(NexusRuntime.Logger.IsEnabled, "a release player's logger drops LogWarning");
                var throttler = new SaveThrottler();
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[SaveThrottler\] Save execution failed \(1/5 consecutive\): disk full"));

                throttler.ForceSave("economy", () => throw new System.IO.IOException("disk full"));

                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
            }
        }

        [Test]
        public void WarnAlways_UsesTheRegisteredLogger_WhileItIsEnabled()
        {
            var logger = new RecordingLogger();

            NexusLog.WarnAlways(logger, "[AdMob] Rewarded ad did not load");

            Assert.AreEqual("[AdMob] Rewarded ad did not load", logger.LastWarning, "a game's own logger (crash reporting) still receives it");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
