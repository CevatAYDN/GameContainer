using System;
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
