#if NEXUS_FIREBASE_ANALYTICS
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
    /// and parameter values it has no type for, are never sent.
    /// </summary>
    public sealed class FirebaseAnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        private readonly AnalyticsGate _gate = new AnalyticsGate();
        private readonly Dictionary<string, string> _heldProperties = new Dictionary<string, string>();
        private bool _ready;

        [Inject] public IAnalyticsConsent Consent { get; set; }

        private ConsentState State => _ready ? Consent.State : ConsentState.Unknown;

        private AnalyticsChoice Choice => _ready ? Consent.Choice : AnalyticsChoice.Unknown;

        public override async ValueTask InitializeAsync(CancellationToken ct)
        {
            FirebaseDependencyStatus status = await FirebaseAppSdk.CheckAndFixDependenciesAsync();
            if (status != FirebaseDependencyStatus.Available)
            {
                NexusLog.WarnAlways("[Firebase] Analytics is off: the SDK's dependencies are " + status);
                return;
            }

            FirebaseAnalyticsSdk.SetAnalyticsCollectionEnabled(false);
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
            if (AnalyticsEventName.IsValid(eventName))
            {
                _gate.Log(State, Choice, eventName, parameters, Send);
            }
        }

        public void SetUserProperty(string key, string value)
        {
            if (!AnalyticsUserProperty.IsValidName(key) || !AnalyticsUserProperty.IsValidValue(value))
            {
                return;
            }

            if (AnalyticsCollectionPolicy.Decide(State, Choice) == AnalyticsDecision.Send)
            {
                FirebaseAnalyticsSdk.SetUserProperty(key, value);
                return;
            }

            _heldProperties[key] = value;
        }

        public override void Dispose()
        {
            if (_ready)
            {
                Consent.Changed -= Apply;
            }
        }

        private void Apply()
        {
            AnalyticsDecision decision = AnalyticsCollectionPolicy.Decide(Consent.State, Consent.Choice);
            FirebaseAnalyticsSdk.SetAnalyticsCollectionEnabled(decision == AnalyticsDecision.Send);
            _gate.Resolve(Consent.State, Consent.Choice, Send);

            if (decision == AnalyticsDecision.Send)
            {
                foreach (KeyValuePair<string, string> property in _heldProperties)
                {
                    FirebaseAnalyticsSdk.SetUserProperty(property.Key, property.Value);
                }
            }

            if (decision != AnalyticsDecision.Hold)
            {
                _heldProperties.Clear();
            }
        }

        private static void Send(AnalyticsEvent analyticsEvent)
        {
            var parameters = new List<FirebaseParameter>();
            if (analyticsEvent.Parameters != null)
            {
                foreach (KeyValuePair<string, object> parameter in analyticsEvent.Parameters)
                {
                    if (AnalyticsEventName.IsValidParameterName(parameter.Key) && TryConvert(parameter.Key, parameter.Value, out FirebaseParameter converted))
                    {
                        parameters.Add(converted);
                    }
                }
            }

            FirebaseAnalyticsSdk.LogEvent(analyticsEvent.Name, parameters.ToArray());
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