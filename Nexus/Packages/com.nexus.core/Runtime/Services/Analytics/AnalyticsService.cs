using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Core.Services
{
    public interface IAnalyticsService
    {
        void LogEvent(string eventName);
        void LogEvent(string eventName, Dictionary<string, object> parameters);
        void SetUserProperty(string key, string value);
    }

    public class AnalyticsService : IAnalyticsService, INexusService
    {
        public ValueTask InitializeAsync(CancellationToken ct) => default;
        public void OnDispose() { }

        public void LogEvent(string eventName)
        {
            NexusRuntime.Logger?.Log($"[NexusAnalytics] Event: {eventName}");
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            var pList = new List<string>();
            if (parameters != null)
            {
                foreach (var kvp in parameters)
                    pList.Add($"{kvp.Key}: {kvp.Value}");
            }
            NexusRuntime.Logger?.Log($"[NexusAnalytics] Event: {eventName} | Params: {string.Join(", ", pList)}");
        }

        public void SetUserProperty(string key, string value)
        {
            NexusRuntime.Logger?.Log($"[NexusAnalytics] UserProperty: {key} = {value}");
        }
    }
}
