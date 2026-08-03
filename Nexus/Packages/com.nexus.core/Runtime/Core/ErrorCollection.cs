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

        // ConcurrentQueue guarantees FIFO ordering: when the cap is exceeded the OLDEST
        // entries are evicted first (TryDequeue), which is the semantically correct behaviour
        // for a rolling error buffer. ConcurrentBag had random eviction order (LIFO-biased)
        // so recent errors were silently discarded while old ones were kept.
        private static readonly ConcurrentQueue<ErrorEntry> s_errors = new();
        private static readonly Dictionary<string, int> s_errorGrouping = new();
        // Guards both s_errorGrouping mutations and the s_errors enqueue call so that
        // grouping counters and the queue stay in sync even under concurrent writers.
        private static readonly object s_addLock = new();
        private static int s_nextId = 1;
        private static int s_maxErrors = 1000;
        // A10 fix: s_errorGrouping previously grew unboundedly — every unique
        // "category:message" key stayed in memory forever even after its entries were
        // pruned, so a long session with dynamic error messages (URLs, ids, filenames)
        // leaked memory. Cap it and rebuild from the retained queue when exceeded.
        private const int MaxGroupingKeys = 4096;
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

        /// <summary>Current unique grouping keys retained (A10 testability accessor).</summary>
        internal static int GroupingKeyCount => s_errorGrouping.Count;

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

            // BUG-16 fix: group update and queue.Enqueue are now atomic under s_addLock so that
            // grouping counters and the queue always stay consistent even under concurrent writers.
            var key = $"{category}:{message}";
            lock (s_addLock)
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

                s_errors.Enqueue(entry);

                // Prune OLDEST errors when over limit (ConcurrentQueue.TryDequeue removes
                // the front/oldest element — semantically correct for a rolling buffer).
                while (s_errors.Count > s_maxErrors)
                {
                    s_errors.TryDequeue(out _);
                }

                // A10 fix: bound the grouping counters. When the unique-key cap is hit,
                // rebuild the counters from the retained queue so the dictionary cannot grow
                // without bound while Count semantics stay consistent with what is retained.
                if (s_errorGrouping.Count > MaxGroupingKeys)
                {
                    s_errorGrouping.Clear();
                    foreach (var retained in s_errors)
                    {
                        var retainedKey = $"{retained.Category}:{retained.Message}";
                        s_errorGrouping[retainedKey] = s_errorGrouping.TryGetValue(retainedKey, out var cnt) ? cnt + 1 : 1;
                    }
                }
            }

            // T5 fix: a throwing event subscriber must never break error collection itself
            // (an exception here would propagate into signal dispatch / recovery handling,
            // masking the original error). Raise each subscriber individually so one bad
            // handler cannot prevent the others from running, and log — never swallow
            // silently.
            RaiseErrorAdded(entry);
            RaiseErrorCountChanged(s_errors.Count, s_maxErrors);

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
            // BUG-15 fix: take a thread-safe snapshot first so LINQ runs on stable data
            // even if another thread calls Collect() concurrently.
            var snapshot = s_errors.ToArray();
            var query = snapshot.AsEnumerable();

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
            // BUG-15 fix: snapshot before LINQ to avoid concurrent mutation.
            return s_errors.ToArray().OrderByDescending(e => e.Timestamp).Take(count).ToArray();
        }

        public static Dictionary<ErrorCategory, int> GetErrorCounts()
        {
            var snapshot = s_errors.ToArray();
            return snapshot.GroupBy(e => e.Category)
                           .ToDictionary(g => g.Key, g => g.Count());
        }

        public static Dictionary<ErrorSeverity, int> GetSeverityCounts()
        {
            var snapshot = s_errors.ToArray();
            return snapshot.GroupBy(e => e.Severity)
                           .ToDictionary(g => g.Key, g => g.Count());
        }

        public static Dictionary<ErrorCategory, int> GetCategoryCounts()
        {
            var snapshot = s_errors.ToArray();
            return snapshot.GroupBy(e => e.Category)
                           .ToDictionary(g => g.Key, g => g.Count());
        }

        public static void Clear()
        {
            lock (s_addLock)
            {
                // ConcurrentQueue has no Clear() in .NET Standard 2.0; drain it manually.
                while (s_errors.TryDequeue(out _)) { }
                s_errorGrouping.Clear();
            }
            RaiseErrorCountChanged(0, s_maxErrors);
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

        // Re-entrancy guard: Unity's logMessageReceivedThreaded can be called recursively
        // if an OnErrorAdded subscriber calls Debug.Log/LogError. ThreadStatic ensures
        // each thread tracks its own depth independently — thread A logging cannot block
        // thread B's callback, and a subscriber exception on thread A does not corrupt B's guard.
        [System.ThreadStatic]
        private static bool s_inLogCallback;

        private static void OnUnityLogReceived(string condition, string stackTrace, LogType type)
        {
            if (!s_enabled) return;
            // Guard against infinite loop: if a subscriber of OnErrorAdded calls Debug.Log,
            // that re-enters this callback. Without the guard, the call stack grows until
            // a StackOverflowException crashes the process.
            if (s_inLogCallback) return;
            s_inLogCallback = true;
            try
            {
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
            finally
            {
                s_inLogCallback = false;
            }
        }

        public static void ClearBefore(DateTime timestamp)
        {
            // Rebuild the queue atomically under s_addLock, keeping only entries
            // that are not older than the cutoff timestamp.
            lock (s_addLock)
            {
                var toKeep = s_errors.ToArray().Where(e => e.Timestamp >= timestamp).ToList();
                while (s_errors.TryDequeue(out _)) { }
                s_errorGrouping.Clear();
                foreach (var entry in toKeep)
                {
                    s_errors.Enqueue(entry);
                    var key = $"{entry.Category}:{entry.Message}";
                    s_errorGrouping[key] = s_errorGrouping.TryGetValue(key, out var cnt) ? cnt + 1 : 1;
                }
            }
        }

        public static ErrorEntry[] GetFrequentErrors(int minCount = 3, int limit = 10)
        {
            // BUG-15 fix: snapshot before LINQ.
            return s_errors.ToArray()
                           .Where(e => e.Count >= minCount)
                           .OrderByDescending(e => e.Count)
                           .Take(limit)
                           .ToArray();
        }

        // ── Safe event raising (T5) ────────────────────────────────────────
        // Event subscribers are third-party code; a throwing subscriber must not propagate
        // out of Collect()/Clear() (which run inside error/recovery/signal paths) nor prevent
        // other subscribers from receiving the event. Each subscriber runs in its own
        // try/catch and failures are logged to the Unity console.

        private static void RaiseErrorAdded(ErrorEntry entry)
        {
            var handler = OnErrorAdded;
            if (handler == null) return;
            foreach (Action<ErrorEntry> subscriber in handler.GetInvocationList())
            {
                try { subscriber(entry); }
                catch (Exception ex)
                {
                    Debug.LogError($"[Nexus] OnErrorAdded subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void RaiseErrorCountChanged(int current, int max)
        {
            var handler = OnErrorCountChanged;
            if (handler == null) return;
            foreach (Action<int, int> subscriber in handler.GetInvocationList())
            {
                try { subscriber(current, max); }
                catch (Exception ex)
                {
                    Debug.LogError($"[Nexus] OnErrorCountChanged subscriber threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
