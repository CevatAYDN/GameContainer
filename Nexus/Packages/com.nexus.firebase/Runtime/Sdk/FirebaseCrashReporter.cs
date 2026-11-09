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
        private volatile bool _disposed;

        // Lazy, instance-local SDK calls: construction does not start Firebase.
        internal Func<Task<FirebaseDependencyStatus>> CheckDependenciesAsync = () => FirebaseAppSdk.CheckAndFixDependenciesAsync();
        internal Action<bool> CollectionEnabledSink = enabled => FirebaseCrashlyticsSdk.IsCrashlyticsCollectionEnabled = enabled;
        internal Action<string, string> CustomKeySink = (key, value) => FirebaseCrashlyticsSdk.SetCustomKey(key, value);
        internal Action<string> LogSink = message => FirebaseCrashlyticsSdk.Log(message);
        internal Action<Exception> ExceptionSink = exception => FirebaseCrashlyticsSdk.LogException(exception);

        [Inject] public IAnalyticsConsent Consent { get; set; }

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return;
            ct.ThrowIfCancellationRequested();
            // Listening before the SDK starts: an error logged meanwhile is the likeliest configuration failure, and the context
            // holds it until the SDK is ready (issue #171).
            Application.logMessageReceivedThreaded += OnLog;
            _commandFailures = SignalBus.Subscribe<CommandFailedSignal>(failed => RecordNonFatal(failed.Exception));

            try
            {
                FirebaseDependencyStatus status = await CheckDependenciesAsync();
                if (_disposed) return;
                ct.ThrowIfCancellationRequested();
                if (status != FirebaseDependencyStatus.Available)
                {
                    NexusLog.WarnAlways("[Firebase] Crashlytics is off: the SDK's dependencies are " + status);
                    _context.Close();
                    return;
                }

                _ready = true;
                Consent.Changed += Apply;
                Apply();
                _context.Open(CustomKeySink, LogSink, Report);
            }
            finally
            {
                if (!_ready)
                {
                    Application.logMessageReceivedThreaded -= OnLog;
                    _commandFailures?.Dispose();
                    _commandFailures = null;
                }
            }
        }

        public void RecordNonFatal(Exception exception)
        {
            if (!_disposed) _context.RecordNonFatal(exception);
        }

        private void Report(Exception exception)
        {
            if (_disposed || _reporting)
            {
                return;
            }

            // Crashlytics may log through Unity itself; without this guard that log would be reported again, without end.
            _reporting = true;
            try
            {
                ExceptionSink(exception);
            }
            finally
            {
                _reporting = false;
            }
        }

        public void SetCustomKey(string key, string value)
        {
            if (!_disposed) _context.SetCustomKey(key, value);
        }

        public void Log(string message)
        {
            if (!_disposed) _context.Log(message);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Application.logMessageReceivedThreaded -= OnLog;
            _commandFailures?.Dispose();
            _commandFailures = null;
            if (_ready)
            {
                Consent.Changed -= Apply;
            }
            _ready = false;
            _context.Close();
        }

        private void Apply()
        {
            if (_disposed) return;
            CollectionEnabledSink(
                AnalyticsCollectionPolicy.Decide(Consent.State, Consent.Choice) == AnalyticsDecision.Send);
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
