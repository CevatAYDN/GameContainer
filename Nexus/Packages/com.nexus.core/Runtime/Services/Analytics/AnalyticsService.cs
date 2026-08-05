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

    // R2026-H1 fix: derives from NexusService<IAnalyticsService> like every other service
    // (previously implemented INexusService directly — inconsistent base-class usage).
    [StubService("Replace with Firebase Analytics or Amplitude before release")]
    public class AnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
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
