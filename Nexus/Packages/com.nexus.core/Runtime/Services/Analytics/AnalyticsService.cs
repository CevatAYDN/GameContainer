using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface IAnalyticsService
    {
        void LogEvent(string eventName);
        void LogEvent(string eventName, Dictionary<string, object> parameters);
        void SetUserProperty(string key, string value);
    }

    // Derives from NexusService<IAnalyticsService> like every other service
    // (previously implemented INexusService directly — inconsistent base-class usage).
    // It only logs: with the log dropped (a release player) it builds no string or list (#314 R3-07).
    [Preserve]
    public class AnalyticsService : NexusService<IAnalyticsService>, IAnalyticsService
    {
        public void LogEvent(string eventName)
        {
            var logger = NexusRuntime.Logger;
            if (logger == null || !logger.IsEnabled) return;
            logger.Log($"[NexusAnalytics] Event: {eventName}");
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            var logger = NexusRuntime.Logger;
            if (logger == null || !logger.IsEnabled) return;
            var pList = new List<string>();
            if (parameters != null)
            {
                foreach (var kvp in parameters)
                    pList.Add($"{kvp.Key}: {kvp.Value}");
            }
            logger.Log($"[NexusAnalytics] Event: {eventName} | Params: {string.Join(", ", pList)}");
        }

        public void SetUserProperty(string key, string value)
        {
            var logger = NexusRuntime.Logger;
            if (logger == null || !logger.IsEnabled) return;
            logger.Log($"[NexusAnalytics] UserProperty: {key} = {value}");
        }
    }
}
