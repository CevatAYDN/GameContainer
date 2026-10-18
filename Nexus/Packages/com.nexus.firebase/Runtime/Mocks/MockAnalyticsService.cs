using System.Collections.Generic;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Mocks
{
    /// <summary>
    /// Records what the game sends, applying the event-name rule of the Firebase service, for the editor, development builds and tests.
    /// </summary>
    public sealed class MockAnalyticsService : IAnalyticsService
    {
        private readonly List<RecordedEvent> _events = new();
        private readonly Dictionary<string, string> _userProperties = new();

        public IReadOnlyList<RecordedEvent> Events => _events;

        public IReadOnlyDictionary<string, string> UserProperties => _userProperties;

        public void LogEvent(string eventName)
        {
            LogEvent(eventName, null);
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            if (AnalyticsEventName.IsValid(eventName))
            {
                _events.Add(new RecordedEvent(eventName, parameters));
            }
        }

        public void SetUserProperty(string key, string value)
        {
            _userProperties[key] = value;
        }
    }

    public readonly struct RecordedEvent
    {
        public RecordedEvent(string name, IReadOnlyDictionary<string, object> parameters)
        {
            Name = name;
            Parameters = parameters;
        }

        public string Name { get; }

        public IReadOnlyDictionary<string, object> Parameters { get; }
    }
}
