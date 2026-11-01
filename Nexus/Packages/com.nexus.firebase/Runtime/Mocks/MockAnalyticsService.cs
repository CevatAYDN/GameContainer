using System.Collections.Generic;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Mocks
{
    /// <summary>
    /// Records what the game sends, and drops what the Firebase service would drop: an event name it refuses, a parameter it refuses (its
    /// name, or a value Firebase has no type for), and a user property over its limits; each is reported once, as the service does
    /// (<see cref="AnalyticsRefusals"/>). For the editor, development builds and tests.
    /// </summary>
    public sealed class MockAnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        private readonly List<AnalyticsEvent> _events = new();
        private readonly Dictionary<string, string> _userProperties = new();
        private readonly AnalyticsRefusals _refusals = new();

        public IReadOnlyList<AnalyticsEvent> Events => _events;

        public IReadOnlyDictionary<string, string> UserProperties => _userProperties;

        public void LogEvent(string eventName)
        {
            LogEvent(eventName, null);
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            if (_refusals.AcceptEvent(eventName))
            {
                _events.Add(new AnalyticsEvent(eventName, KeepValidParameters(eventName, parameters)));
            }
        }

        public void SetUserProperty(string key, string value)
        {
            if (_refusals.AcceptUserProperty(key, value))
            {
                _userProperties[key] = value;
            }
        }

        private Dictionary<string, object> KeepValidParameters(string eventName, Dictionary<string, object> parameters)
        {
            if (parameters == null)
            {
                return null;
            }

            var kept = new Dictionary<string, object>(parameters.Count);
            foreach (KeyValuePair<string, object> parameter in parameters)
            {
                if (_refusals.AcceptParameter(eventName, parameter.Key, parameter.Value))
                {
                    kept.Add(parameter.Key, parameter.Value);
                }
            }

            return kept;
        }
    }
}