using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Centralized error collection and reporting system for Nexus Core.
    /// Captures runtime errors, warnings, and informational messages with categorization.
    /// </summary>
    public static class ErrorCollection
    {
        public enum ErrorSeverity
        {
            Info,
            Warning,
            Error,
            Critical
        }

        public enum ErrorCategory
        {
            General,
            Signal,
            Command,
            Service,
            DI,
            Network,
            Performance,
            Memory,
            Unity,
            Custom
        }

        public class ErrorEntry
        {
            public int Id { get; set; }
            public ErrorSeverity Severity { get; set; }
            public ErrorCategory Category { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public string Context { get; set; }
            public DateTime Timestamp { get; set; }
            public int Count { get; set; }
            public string RelatedType { get; set; }
            public Dictionary<string, string> Metadata { get; set; } = new();
        }

        private static readonly ConcurrentBag<ErrorEntry> s_errors = new();
        private static readonly Dictionary<string, int> s_errorGrouping = new();
        private static int s_nextId = 1;
        private static int s_maxErrors = 1000;
        private static bool s_enabled = true;

        public static event Action<ErrorEntry> OnErrorAdded;
        public static event Action<int, int> OnErrorCountChanged; // (current, max)

        public static bool Enabled
        {
            get => s_enabled;
            set => s_enabled = value;
        }

        public static int MaxErrors
        {
            get => s_maxErrors;
            set => s_maxErrors = Math.Max(100, value);
        }

        public static int TotalErrorCount => s_errors.Count;

        public static void Collect(ErrorSeverity severity, ErrorCategory category, string message, string stackTrace = null, string context = null, string relatedType = null, bool logToConsole = true)
        {
            if (!s_enabled) return;

            var entry = new ErrorEntry
            {
                Id = System.Threading.Interlocked.Increment(ref s_nextId),
                Severity = severity,
                Category = category,
                Message = message,
                StackTrace = stackTrace ?? UnityEngine.StackTraceUtility.ExtractStackTrace(),
                Context = context,
                Timestamp = DateTime.Now,
                RelatedType = relatedType
            };

            // Group similar errors
            var key = $"{category}:{message}";
            lock (s_errorGrouping)
            {
                if (s_errorGrouping.ContainsKey(key))
                {
                    entry.Count = s_errorGrouping[key] + 1;
                    s_errorGrouping[key] = entry.Count;
                }
                else
                {
                    entry.Count = 1;
                    s_errorGrouping[key] = 1;
                }
            }

            s_errors.Add(entry);

            // Prune old errors if over limit
            while (s_errors.Count > s_maxErrors)
            {
                s_errors.TryTake(out _);
            }

            OnErrorAdded?.Invoke(entry);
            OnErrorCountChanged?.Invoke(s_errors.Count, s_maxErrors);

            // Log to Unity console for backward compatibility (unless disabled)
            if (logToConsole)
            {
                switch (severity)
                {
                    case ErrorSeverity.Info:
                        Debug.Log($"[Nexus Info] {message}");
                        break;
                    case ErrorSeverity.Warning:
                        Debug.LogWarning($"[Nexus Warning] {message}");
                        break;
                    case ErrorSeverity.Error:
                        Debug.LogError($"[Nexus Error] {message}");
                        break;
                    case ErrorSeverity.Critical:
                        Debug.LogError($"[Nexus CRITICAL] {message}");
                        break;
                }
            }
        }

        public static void CollectException(Exception ex, ErrorCategory category = ErrorCategory.General, string context = null)
        {
            // Don't log expected system exceptions to Unity Console
            // These are part of the normal error handling flow
            bool shouldLogToConsole = !(ex is NexusReentrancyException || ex is NexusAsyncOverflowException);

            Collect(ErrorSeverity.Error, category, ex.Message, ex.StackTrace, context, ex.GetType().Name, shouldLogToConsole);
        }

        public static void CollectException(Exception ex, ErrorCategory category, string context, bool logToConsole)
        {
            Collect(ErrorSeverity.Error, category, ex.Message, ex.StackTrace, context, ex.GetType().Name, logToConsole);
        }

        public static ErrorEntry[] GetErrors(ErrorSeverity? minSeverity = null, ErrorCategory? category = null, int limit = 100)
        {
            var query = s_errors.AsEnumerable();

            if (minSeverity.HasValue)
            {
                query = query.Where(e => e.Severity >= minSeverity.Value);
            }

            if (category.HasValue)
            {
                query = query.Where(e => e.Category == category.Value);
            }

            return query.OrderByDescending(e => e.Timestamp).Take(limit).ToArray();
        }

        public static ErrorEntry[] GetRecentErrors(int count = 20)
        {
            return s_errors.OrderByDescending(e => e.Timestamp).Take(count).ToArray();
        }

        public static Dictionary<ErrorCategory, int> GetErrorCounts()
        {
            return s_errors.GroupBy(e => e.Category)
                          .ToDictionary(g => g.Key, g => g.Count());
        }

        public static Dictionary<ErrorSeverity, int> GetSeverityCounts()
        {
            return s_errors.GroupBy(e => e.Severity)
                          .ToDictionary(g => g.Key, g => g.Count());
        }

        public static Dictionary<ErrorCategory, int> GetCategoryCounts()
        {
            return s_errors.GroupBy(e => e.Category)
                          .ToDictionary(g => g.Key, g => g.Count());
        }

        public static void Clear()
        {
            s_errors.Clear();
            lock (s_errorGrouping)
            {
                s_errorGrouping.Clear();
            }
            OnErrorCountChanged?.Invoke(0, s_maxErrors);
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeLogHook()
        {
            Application.logMessageReceivedThreaded -= OnUnityLogReceived;
            Application.logMessageReceivedThreaded += OnUnityLogReceived;
        }

        private static void OnUnityLogReceived(string condition, string stackTrace, LogType type)
        {
            if (!s_enabled) return;

            ErrorSeverity severity = type switch
            {
                LogType.Log => ErrorSeverity.Info,
                LogType.Warning => ErrorSeverity.Warning,
                LogType.Error => ErrorSeverity.Error,
                LogType.Assert => ErrorSeverity.Error,
                LogType.Exception => ErrorSeverity.Critical,
                _ => ErrorSeverity.Info
            };

            // Avoid loop: logToConsole MUST be false when capturing from Unity Log
            Collect(severity, ErrorCategory.Unity, condition, stackTrace, "Unity Log", null, logToConsole: false);
        }

        public static void ClearBefore(DateTime timestamp)
        {
            var toRemove = s_errors.Where(e => e.Timestamp < timestamp).ToList();
            foreach (var error in toRemove)
            {
                s_errors.TryTake(out _);
            }
        }

        public static ErrorEntry[] GetFrequentErrors(int minCount = 3, int limit = 10)
        {
            return s_errors.Where(e => e.Count >= minCount)
                         .OrderByDescending(e => e.Count)
                         .Take(limit)
                         .ToArray();
        }
    }
}
