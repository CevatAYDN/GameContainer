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

        public interface IUnboundDependency { }

        public sealed class NeedsAnUnboundDependency
        {
            public readonly IUnboundDependency Dependency;
            public NeedsAnUnboundDependency(IUnboundDependency dependency) => Dependency = dependency;
        }

        public struct ReleaseLogSignal { }

        // Issue #164: a binding injected as null, a rebind and a behaviour with no context to fire into or inject from were warned
        // through LogWarning, which a release player drops; Inject with no context said nothing at all
        [Test]
        public void AMissingBindingARebindAndAMissingContext_AreWarnedInAReleasePlayerToo()
        {
            NexusRuntime.Reset(); // the fallback logger, which a release player disables; no context is registered
            NexusBuild.OverrideForTests(false);
            var go = new GameObject("no context");
            try
            {
                Assert.IsFalse(NexusRuntime.Logger.IsEnabled, "a release player's logger drops LogWarning");
                using (var di = new NexusDI())
                {
                    di.Bind<NeedsAnUnboundDependency>();
                    LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Constructor parameter 0 \('.*IUnboundDependency'\) on '.*NeedsAnUnboundDependency' is not registered; passing null"));
                    Assert.IsNull(di.Resolve<NeedsAnUnboundDependency>().Dependency);

                    LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Rebinding '.*NeedsAnUnboundDependency'"));
                    di.Bind<NeedsAnUnboundDependency>(Lifetime.Transient);
                }

                var behaviour = go.AddComponent<ReleaseLogBehaviour>();
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Cannot Fire<ReleaseLogSignal>: No active Nexus Context found"));
                behaviour.Fire(new ReleaseLogSignal());
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Cannot Inject 'ReleaseLogBehaviour': No active Nexus Context found"));
                behaviour.Inject();
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
                UnityEngine.Object.DestroyImmediate(go);
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

    public sealed class ReleaseLogBehaviour : NexusBehaviour { }
}
