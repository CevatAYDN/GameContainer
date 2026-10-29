using System.Collections.Generic;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Mocks
{
    /// <summary>
    /// Records what the game sends, and drops what the Firebase service would drop: an event name it refuses, a parameter name it refuses,
    /// and a user property over its limits. For the editor, development builds and tests.
    /// </summary>
    public sealed class MockAnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        private readonly List<AnalyticsEvent> _events = new();
        private readonly Dictionary<string, string> _userProperties = new();

        public IReadOnlyList<AnalyticsEvent> Events => _events;

        public IReadOnlyDictionary<string, string> UserProperties => _userProperties;

        public void LogEvent(string eventName)
        {
            LogEvent(eventName, null);
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            if (AnalyticsEventName.IsValid(eventName))
            {
                _events.Add(new AnalyticsEvent(eventName, KeepValidParameters(parameters)));
            }
        }

        public void SetUserProperty(string key, string value)
        {
            if (AnalyticsUserProperty.IsValidName(key) && AnalyticsUserProperty.IsValidValue(value))
            {
                _userProperties[key] = value;
            }
        }

        private static Dictionary<string, object> KeepValidParameters(Dictionary<string, object> parameters)
        {
            if (parameters == null)
            {
                return null;
            }

            var kept = new Dictionary<string, object>(parameters.Count);
            foreach (KeyValuePair<string, object> parameter in parameters)
            {
                if (AnalyticsEventName.IsValidParameterName(parameter.Key))
                {
                    kept.Add(parameter.Key, parameter.Value);
                }
            }

            return kept;
        }
    }
}