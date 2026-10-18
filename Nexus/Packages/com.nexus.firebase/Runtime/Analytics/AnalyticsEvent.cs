using System.Collections.Generic;

namespace Nexus.Firebase.Analytics
{
    /// <summary>One analytics event as the game logged it, with its parameters copied so a caller may reuse its own dictionary.</summary>
    public readonly struct AnalyticsEvent
    {
        public AnalyticsEvent(string name, Dictionary<string, object> parameters)
        {
            Name = name;
            Parameters = parameters == null ? null : new Dictionary<string, object>(parameters);
        }

        public string Name { get; }

        public IReadOnlyDictionary<string, object> Parameters { get; }
    }
}