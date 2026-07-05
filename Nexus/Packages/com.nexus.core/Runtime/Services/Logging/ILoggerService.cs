using System;

namespace Nexus.Core.Services
{
    public interface ILoggerService
    {
        void Log(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogException(Exception exception);
        bool IsEnabled { get; set; }
    }
}
