using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Standardized log entry with structured context for every Nexus log message.
    /// Error Visibility & Standardized Logging.
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
    /// Error Visibility & Standardized Logging.
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
            // Pass the full exception (with stack trace) via LogException.
            // Previously only ex.Message was logged, making production debugging impossible.
            if (s_instance != null)
                s_instance.LogException(ex);
            else
                Debug.LogException(ex);
            var msg = ex?.Message ?? "null";
            var entry = new NexusLogEntry(source, operation, contextId, msg);
            s_instance?.LogError(entry.Format());
        }

        public static void Error(string source, string operation, string contextId, string message, Exception ex)
        {
            // Preserve stack trace via LogException in addition to the formatted message.
            var entry = new NexusLogEntry(source, operation, contextId, ex != null ? string.Concat(message, " | ", ex.Message) : message);
            if (s_instance != null)
            {
                s_instance.LogException(ex);
                s_instance.LogError(entry.Format());
            }
            else if (ex != null)
            {
                Debug.LogException(ex);
            }
            else
            {
                Debug.LogError(entry.Format());
            }
        }

        public static void Warn(string source, string operation, string contextId, string message)
        {
            var entry = new NexusLogEntry(source, operation, contextId, message);
            if (s_instance != null)
                s_instance.LogWarning(entry.Format());
            else
                Debug.LogWarning(entry.Format());
        }

        /// <summary>
        /// A warning that must reach the player log in release builds too, where
        /// <see cref="LoggerService"/> drops <c>Log</c> and <c>LogWarning</c>: failures at an SDK
        /// boundary (an ad that did not load, a store that is not reachable). Goes to the registered
        /// logger while it is enabled, otherwise straight to Unity's log (logcat on Android).
        /// </summary>
        public static void WarnAlways(string message) => WarnAlways(NexusRuntime.Logger ?? s_instance, message);

        internal static void WarnAlways(ILoggerService logger, string message)
        {
            if (logger != null && logger.IsEnabled)
                logger.LogWarning(message);
            else
                Debug.LogWarning(message);
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

    [Preserve]
    public class LoggerService : NexusService<ILoggerService>, ILoggerService
    {
        public bool IsEnabled { get; set; } = true;

        /// <summary>Release builds (not editor, not development) drop Log and LogWarning; errors and exceptions always pass. See <see cref="NexusLog.WarnAlways"/>.</summary>
        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (!NexusBuild.IsDevelopment) IsEnabled = false;
            NexusLog.Initialize(this);
            return default;
        }

        public override void OnDispose()
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
