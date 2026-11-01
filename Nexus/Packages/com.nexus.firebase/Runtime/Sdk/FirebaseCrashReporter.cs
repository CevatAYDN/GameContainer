#if NEXUS_FIREBASE_CRASHLYTICS
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;
using Nexus.Firebase.Crash;
using UnityEngine;
using FirebaseAppSdk = global::Firebase.FirebaseApp;
using FirebaseCrashlyticsSdk = global::Firebase.Crashlytics.Crashlytics;
using FirebaseDependencyStatus = global::Firebase.DependencyStatus;

namespace Nexus.Firebase.Sdk
{
    /// <summary>
    /// Crashlytics behind <see cref="ICrashReporter"/>. It reports the commands Nexus's recovery engine gave up on, and the errors the game logs
    /// (Debug.LogError), as non-fatals, with the game's custom keys and log lines. Collection follows the same consent decision as analytics.
    /// </summary>
    public sealed class FirebaseCrashReporter : NexusService<ICrashReporter>, ICrashReporter
    {
        [ThreadStatic] private static bool _reporting;

        private readonly CrashContext _context = new CrashContext();
        private ISignalSubscription _commandFailures;
        private bool _ready;

        [Inject] public IAnalyticsConsent Consent { get; set; }

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            FirebaseDependencyStatus status = await FirebaseAppSdk.CheckAndFixDependenciesAsync();
            if (status != FirebaseDependencyStatus.Available)
            {
                NexusLog.WarnAlways("[Firebase] Crashlytics is off: the SDK's dependencies are " + status);
                _context.Close();
                return;
            }

            _ready = true;
            Consent.Changed += Apply;
            Apply();
            _context.Open(FirebaseCrashlyticsSdk.SetCustomKey, FirebaseCrashlyticsSdk.Log);
            _commandFailures = SignalBus.Subscribe<CommandFailedSignal>(failed => RecordNonFatal(failed.Exception));
            Application.logMessageReceivedThreaded += OnLog;
        }

        public void RecordNonFatal(Exception exception)
        {
            if (!_ready || exception == null || _reporting)
            {
                return;
            }

            // Crashlytics may log through Unity itself; without this guard that log would be reported again, without end.
            _reporting = true;
            try
            {
                FirebaseCrashlyticsSdk.LogException(exception);
            }
            finally
            {
                _reporting = false;
            }
        }

        public void SetCustomKey(string key, string value) => _context.SetCustomKey(key, value);

        public void Log(string message) => _context.Log(message);

        public override void Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            _commandFailures?.Dispose();
            if (_ready)
            {
                Consent.Changed -= Apply;
            }
        }

        private void Apply()
        {
            FirebaseCrashlyticsSdk.IsCrashlyticsCollectionEnabled =
                AnalyticsCollectionPolicy.Decide(Consent.State, Consent.Choice) == AnalyticsDecision.Send;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            // Exceptions are Crashlytics's own to report; only the errors the game logged on purpose are turned into non-fatals here.
            if (type == LogType.Error || type == LogType.Assert)
            {
                RecordNonFatal(new LoggedErrorException(condition, stackTrace));
            }
        }
    }
}
#endif