#if NEXUS_FIREBASE_ANALYTICS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;
using FirebaseAnalyticsSdk = global::Firebase.Analytics.FirebaseAnalytics;
using FirebaseAppSdk = global::Firebase.FirebaseApp;
using FirebaseDependencyStatus = global::Firebase.DependencyStatus;
using FirebaseParameter = global::Firebase.Analytics.Parameter;

namespace Nexus.Firebase.Sdk
{
    /// <summary>
    /// Firebase Analytics behind <see cref="IAnalyticsService"/>. Collection stays off until the consent decision allows it; events logged
    /// before that wait in an <see cref="AnalyticsGate"/> and are sent in order, or dropped if analytics were refused. Names Firebase refuses,
    /// and parameter values it has no type for, are never sent, and each is reported once in every build (<see cref="AnalyticsRefusals"/>).
    /// </summary>
    public sealed class FirebaseAnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        private readonly AnalyticsGate _gate = new AnalyticsGate();
        private readonly Dictionary<string, string> _heldProperties = new Dictionary<string, string>();
        private readonly AnalyticsRefusals _refusals = new AnalyticsRefusals();
        private bool _ready;
        private bool _disposed;

        // Lazy, instance-local SDK calls: construction does not start Firebase.
        internal Func<Task<FirebaseDependencyStatus>> CheckDependenciesAsync = () => FirebaseAppSdk.CheckAndFixDependenciesAsync();
        internal Action<bool> CollectionEnabledSink = enabled => FirebaseAnalyticsSdk.SetAnalyticsCollectionEnabled(enabled);
        internal Action<string, string> PropertySink = (key, value) => FirebaseAnalyticsSdk.SetUserProperty(key, value);
        internal Action<string, FirebaseParameter[]> EventSink = (name, parameters) => FirebaseAnalyticsSdk.LogEvent(name, parameters);

        [Inject] public IAnalyticsConsent Consent { get; set; }

        private ConsentState State => _ready ? Consent.State : ConsentState.Unknown;

        private AnalyticsChoice Choice => _ready ? Consent.Choice : AnalyticsChoice.Unknown;

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return;
            ct.ThrowIfCancellationRequested();
            FirebaseDependencyStatus status = await CheckDependenciesAsync();
            if (_disposed) return;
            ct.ThrowIfCancellationRequested();
            if (status != FirebaseDependencyStatus.Available)
            {
                NexusLog.WarnAlways("[Firebase] Analytics is off: the SDK's dependencies are " + status);
                _gate.Close();
                _heldProperties.Clear();
                return;
            }

            CollectionEnabledSink(false);
            _ready = true;
            Consent.Changed += Apply;
            Apply();
        }

        public void LogEvent(string eventName)
        {
            LogEvent(eventName, null);
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            if (_disposed) return;
            if (_refusals.AcceptEvent(eventName))
            {
                _gate.Log(State, Choice, eventName, parameters, Send);
            }
        }

        public void SetUserProperty(string key, string value)
        {
            if (_disposed) return;
            if (!_refusals.AcceptUserProperty(key, value))
            {
                return;
            }

            AnalyticsDecision decision = AnalyticsCollectionPolicy.Decide(State, Choice);
            if (decision == AnalyticsDecision.Send)
            {
                PropertySink(key, value);
                return;
            }

            if (decision == AnalyticsDecision.Hold) _heldProperties[key] = value;
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ready)
            {
                Consent.Changed -= Apply;
            }
            _ready = false;
            _gate.Close();
            _heldProperties.Clear();
        }

        private void Apply()
        {
            if (_disposed) return;
            AnalyticsDecision decision = AnalyticsCollectionPolicy.Decide(Consent.State, Consent.Choice);
            CollectionEnabledSink(decision == AnalyticsDecision.Send);
            _gate.Resolve(Consent.State, Consent.Choice, Send);

            if (decision == AnalyticsDecision.Send)
            {
                foreach (KeyValuePair<string, string> property in _heldProperties)
                {
                    PropertySink(property.Key, property.Value);
                }
            }

            if (decision != AnalyticsDecision.Hold)
            {
                _heldProperties.Clear();
            }
        }

        private void Send(AnalyticsEvent analyticsEvent)
        {
            if (_disposed) return;
            var parameters = new List<FirebaseParameter>();
            if (analyticsEvent.Parameters != null)
            {
                foreach (KeyValuePair<string, object> parameter in analyticsEvent.Parameters)
                {
                    if (_refusals.AcceptParameter(analyticsEvent.Name, parameter.Key, parameter.Value) && TryConvert(parameter.Key, parameter.Value, out FirebaseParameter converted))
                    {
                        parameters.Add(converted);
                    }
                }
            }

            EventSink(analyticsEvent.Name, parameters.ToArray());
        }

        private static bool TryConvert(string name, object value, out FirebaseParameter converted)
        {
            switch (AnalyticsParameterValue.KindOf(value))
            {
                case AnalyticsParameterKind.Text:
                    converted = new FirebaseParameter(name, (string)value);
                    return true;
                case AnalyticsParameterKind.Integer:
                    converted = new FirebaseParameter(name, AnalyticsParameterValue.ToInteger(value));
                    return true;
                case AnalyticsParameterKind.Real:
                    converted = new FirebaseParameter(name, AnalyticsParameterValue.ToReal(value));
                    return true;
                default:
                    converted = null;
                    return false;
            }
        }
    }
}
#endif
