using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public class LoggerService : ILoggerService, INexusService
    {
        public bool IsEnabled { get; set; } = true;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            // Build alındığında logları otomatik kapat
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            IsEnabled = false;
#endif
            return default;
        }

        public void OnDispose() { }

        public void Log(string message)
        {
            if (!IsEnabled) return;
            Debug.Log(message);
        }

        public void LogWarning(string message)
        {
            if (!IsEnabled) return;
            Debug.LogWarning(message);
        }

        public void LogError(string message)
        {
            if (!IsEnabled) return;
            Debug.LogError(message);
        }

        public void LogException(Exception exception)
        {
            if (!IsEnabled) return;
            Debug.LogException(exception);
        }
    }
}
