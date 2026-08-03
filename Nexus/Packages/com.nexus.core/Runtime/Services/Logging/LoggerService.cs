using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Standardized log entry with structured context for every Nexus log message.
    /// Plan §7 — Error Visibility & Standardized Logging.
    /// </summary>
    public readonly struct NexusLogEntry
    {
        public string Source { get; }
        public string Operation { get; }
        public string ContextId { get; }
        public string Message { get; }

        public NexusLogEntry(string source, string operation, string contextId, string message)
        {
            Source = source ?? "?";
            Operation = operation ?? "?";
            ContextId = contextId ?? "?";
            Message = message ?? "";
        }

        [ThreadStatic] private static StringBuilder s_formatBuilder;

        public string Format()
        {
            var sb = s_formatBuilder ??= new StringBuilder(128);
            sb.Clear();
            sb.Append("[Nexus]");
            sb.Append('[').Append(Source).Append(']');
            sb.Append('[').Append(Operation).Append(']');
            if (!string.IsNullOrEmpty(ContextId) && ContextId != "?")
                sb.Append('[').Append(ContextId).Append(']');
            sb.Append(' ').Append(Message);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Centralized logging with structured context. Every Nexus component should use
    /// NexusLog to ensure consistent, searchable log output.
    /// Plan §7 — Error Visibility & Standardized Logging.
    /// </summary>
    public static class NexusLog
    {
        private static ILoggerService s_instance;

        internal static void Initialize(ILoggerService instance)
        {
            s_instance = instance;
        }

        internal static void Reset()
        {
            s_instance = null;
        }

        public static void Error(string source, string operation, string contextId, string message)
        {
            var entry = new NexusLogEntry(source, operation, contextId, message);
            if (s_instance != null)
                s_instance.LogError(entry.Format());
            else
                Debug.LogError(entry.Format());
        }

        public static void Error(string source, string operation, string contextId, Exception ex)
        {
            var msg = ex?.Message ?? "null";
            var entry = new NexusLogEntry(source, operation, contextId, msg);
            if (s_instance != null)
                s_instance.LogError(entry.Format());
            else
                Debug.LogException(ex);
        }

        public static void Error(string source, string operation, string contextId, string message, Exception ex)
        {
            var entry = new NexusLogEntry(source, operation, contextId, ex != null ? string.Concat(message, " | ", ex.Message) : message);
            if (s_instance != null)
                s_instance.LogError(entry.Format());
            else
                Debug.LogException(ex);
        }

        public static void Warn(string source, string operation, string contextId, string message)
        {
            var entry = new NexusLogEntry(source, operation, contextId, message);
            if (s_instance != null)
                s_instance.LogWarning(entry.Format());
            else
                Debug.LogWarning(entry.Format());
        }

        public static void Info(string source, string operation, string contextId, string message)
        {
            var entry = new NexusLogEntry(source, operation, contextId, message);
            if (s_instance != null)
                s_instance.Log(entry.Format());
            else
                Debug.Log(entry.Format());
        }
    }

    public class LoggerService : ILoggerService, INexusService
    {
        public bool IsEnabled { get; set; } = true;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            IsEnabled = false;
#endif
            NexusLog.Initialize(this);
            return default;
        }

        public void OnDispose()
        {
            NexusLog.Reset();
        }

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
            Debug.LogError(message);
        }

        public void LogException(Exception exception)
        {
            Debug.LogException(exception);
        }
    }
}
