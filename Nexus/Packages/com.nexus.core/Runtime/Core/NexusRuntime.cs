using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Central registry for all active Nexus contexts.
    /// Provides thread-safe registration, unregistration, enumeration, and domain-reload-safe reset.
    /// </summary>
    public static class NexusRuntime
    {
        public static event System.Action<IContext> OnContextRegistered;
        public static event System.Action<IContext> OnContextUnregistered;

        private static readonly List<IContext> s_activeContexts = new();
        private static readonly HashSet<IContext> s_contextSet = new();
        private static readonly object s_lock = new();
        private static volatile int s_activeContextCount;
        private static List<IContext> s_activeContextsReadOnlyCache = new();
        private static bool s_activeContextsCacheDirty = true;
        private static bool s_monitoringInitialized = false;

        /// <summary>Returns a thread-safe snapshot of all active contexts.</summary>
        /// <remarks>Locked access via <c>s_lock</c>. Returns a snapshot to prevent race conditions during iteration.</remarks>
        public static IReadOnlyList<IContext> ActiveContexts
        {
            get
            {
                lock (s_lock)
                {
                    if (s_activeContextsCacheDirty)
                    {
                        s_activeContextsReadOnlyCache = new List<IContext>(s_activeContexts);
                        s_activeContextsCacheDirty = false;
                    }
                    return s_activeContextsReadOnlyCache;
                }
            }
        }

        /// <summary>
        /// Returns the first registered context, or null if no context has been registered.
        /// </summary>
        public static IContext CurrentContext
        {
            get
            {
                lock (s_lock)
                {
                    return s_activeContexts.Count > 0 ? s_activeContexts[0] : null;
                }
            }
        }

        /// <summary>
        /// Returns the currently active context for the given scope tag.
        /// If scopeTag is null or empty, returns the first matching active context.
        /// </summary>
        public static IContext GetContext(string scopeTag)
        {
            lock (s_lock)
            {
                if (string.IsNullOrEmpty(scopeTag))
                    return CurrentContext;

                for (int i = 0; i < s_activeContexts.Count; i++)
                {
                    var context = s_activeContexts[i];
                    if (context != null && string.Equals(context.ScopeTag, scopeTag, StringComparison.OrdinalIgnoreCase))
                        return context;
                }

                return null;
            }
        }

        /// <summary>
        /// Returns all active contexts that match the provided scope tag.
        /// </summary>
        public static IReadOnlyList<IContext> GetContexts(string scopeTag)
        {
            lock (s_lock)
            {
                if (string.IsNullOrEmpty(scopeTag))
                    return ActiveContexts;

                var matches = new List<IContext>();
                for (int i = 0; i < s_activeContexts.Count; i++)
                {
                    var context = s_activeContexts[i];
                    if (context != null && string.Equals(context.ScopeTag, scopeTag, StringComparison.OrdinalIgnoreCase))
                        matches.Add(context);
                }
                return matches;
            }
        }

        /// <summary>
        /// Safely attempts to resolve a service of type <typeparamref name="T"/> from <see cref="CurrentContext"/>.
        /// Returns null if no context is registered or the service is not registered.
        /// </summary>
        public static T TryResolve<T>() where T : class
        {
            return CurrentContext?.TryResolve<T>();
        }

        /// <summary>
        /// Null-safe accessor for the registered <see cref="Services.ILoggerService"/> from <see cref="CurrentContext"/>.
        /// Returns null if no context or logger service is registered.
        /// </summary>
        public static Services.ILoggerService Logger => CurrentContext?.TryResolve<Services.ILoggerService>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeOnLoad()
        {
            Reset();
        }

        /// <summary>
        /// Creates and registers a pure code-based Context without requiring a Root GameObject in the scene.
        /// Ideal for tests, dedicated servers, or strictly data-oriented architectures.
        /// </summary>
        public static async System.Threading.Tasks.Task<IContext> CreatePureContextAsync(string scopeTag, string[] assemblyScopes = null)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.name = $"{scopeTag}ContextData_Pure";
            data.ScopeTag = scopeTag;
            if (assemblyScopes != null)
            {
                data.AssemblyScopes = assemblyScopes;
            }

            var context = new Context(null, data);
            context.Configure();

            // P1-8 fix: pure contexts run the SAME lifecycle sequence as Root-based
            // contexts — reactive models and services are initialized before the
            // lifecycle Init/Start phases, and ALL configured lifecycles are iterated.
            var ct = context.LifetimeToken;
            await context.InitializeReactiveModelsAsync(ct);
            await context.InitializeServicesAsync(ct);

            var lifecycles = context.ConfiguredLifecycles;
            for (int i = 0; i < lifecycles.Count; i++)
            {
                await lifecycles[i].OnInitializeAsync(ct);
            }
            for (int i = 0; i < lifecycles.Count; i++)
            {
                await lifecycles[i].OnStartAsync(ct);
            }

            return context;
        }

        /// <summary>Disposes all active contexts and clears the registry. Called automatically on domain reload.</summary>
        public static void Reset()
        {
            lock (s_lock)
            {
                for (int i = s_activeContexts.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        s_activeContexts[i].Dispose();
                    }
                    catch (System.Exception ex)
                    {
                        NexusLog.Error(nameof(NexusRuntime), nameof(Reset), string.Empty, ex);
                    }
                }
                s_activeContexts.Clear();
                s_contextSet.Clear();
                s_activeContextsCacheDirty = true;
            }

            NexusDI.ClearCaches();
            Context.ClearAssemblyScanCache();
            Context.ClearDefaultScanAssembliesCache();
            SignalBus.ClearStaticCaches();
            QueuedSignalPoolRegistry.ClearAll();
            Root.ClearRegistry();
            CommandPoolStatics.ClearStateLeakWarnings();
            NexusLog.Reset();
        }

        public static class Metrics
        {
            private static long s_totalSignalsDispatched;
            private static long s_totalCommandsExecuted;

            public static long TotalSignalsDispatched => System.Threading.Interlocked.Read(ref s_totalSignalsDispatched);
            public static long TotalCommandsExecuted => System.Threading.Interlocked.Read(ref s_totalCommandsExecuted);

            public static int ActiveContextCount => s_activeContextCount;

            // Rate fields are written by the runtime (game thread, via SignalBus)
            // and read by the editor (editor thread). Without volatile, the editor
            // can see stale values on ARM platforms with weak memory ordering.
            private static volatile float s_signalsPerSecond;
            private static volatile float s_commandsPerSecond;
            public static float SignalsPerSecond => s_signalsPerSecond;
            public static float CommandsPerSecond => s_commandsPerSecond;

            // _prevSignals/_prevCommands are 64-bit; use Interlocked for atomic
            // access on 32-bit platforms. _lastSampleTime is float; volatile
            // ensures 32-bit atomic write/read on all platforms.
            private static long _prevSignals;
            private static long _prevCommands;
            private static volatile float _lastSampleTime;
            private static readonly object _rateLock = new();

            internal static void RecordSignalDispatched()
            {
                System.Threading.Interlocked.Increment(ref s_totalSignalsDispatched);
            }

            internal static void RecordCommandExecuted()
            {
                System.Threading.Interlocked.Increment(ref s_totalCommandsExecuted);
            }

            // Production tracing ring buffer — always active, no NEXUS_DEBUG needed.
            // TracerPlugin reads this when causal tracing is compiled out.
            private const int TraceBufferSize = 200;
            private static readonly string[] s_traceBuffer = new string[TraceBufferSize];
            private static int s_traceIndex = -1;
            private static int s_traceCount;

            internal static void RecordTrace(string entry)
            {
                int rawIndex = System.Threading.Interlocked.Increment(ref s_traceIndex);
                int idx = ((rawIndex % TraceBufferSize) + TraceBufferSize) % TraceBufferSize;
                s_traceBuffer[idx] = entry;
                if (s_traceCount < TraceBufferSize)
                    System.Threading.Interlocked.Increment(ref s_traceCount);
            }

            public static string[] GetRecentTraces(out int count)
            {
                count = s_traceCount;
                if (count == 0) return System.Array.Empty<string>();
                var result = new string[count];
                int start = ((s_traceIndex - count + 1) % TraceBufferSize + TraceBufferSize) % TraceBufferSize;
                for (int i = 0; i < count; i++)
                    result[i] = s_traceBuffer[(start + i) % TraceBufferSize] ?? "";
                return result;
            }

            public static void UpdateRates()
            {
                // Lock to make the rate calculation atomic across all fields.
                // Metrics are not a hot path so lock overhead is acceptable.
                lock (_rateLock)
                {
                    float now = UnityEngine.Time.time;
                    float delta = now - _lastSampleTime;
                    if (delta > 0.5f)
                    {
                        long currSignals = System.Threading.Interlocked.Read(ref s_totalSignalsDispatched);
                        long currCommands = System.Threading.Interlocked.Read(ref s_totalCommandsExecuted);
                        s_signalsPerSecond = (currSignals - _prevSignals) / delta;
                        s_commandsPerSecond = (currCommands - _prevCommands) / delta;
                        System.Threading.Interlocked.Exchange(ref _prevSignals, currSignals);
                        System.Threading.Interlocked.Exchange(ref _prevCommands, currCommands);
                        _lastSampleTime = now;
                    }
                }
            }
        }

        public static void RegisterContext(IContext context)
        {
            if (context == null) return;

            bool added = false;
            lock (s_lock)
            {
                if (s_contextSet.Add(context))
                {
                    s_activeContexts.Add(context);
                    s_activeContextCount = s_activeContexts.Count;
                    s_activeContextsCacheDirty = true;
                    added = true;
                }
            }
            if (added)
            {
                // Initialize monitoring systems on first context registration
                if (!s_monitoringInitialized)
                {
                    InitializeMonitoring();
                    s_monitoringInitialized = true;
                }

                try
                {
                    OnContextRegistered?.Invoke(context);
                }
                catch (System.Exception ex)
                {
                    NexusLog.Error(nameof(NexusRuntime), nameof(RegisterContext), string.Empty, ex);
                }
            }
        }

        private static void InitializeMonitoring()
        {
            // Enable error collection and performance monitoring by default
            ErrorCollection.Enabled = true;
            PerformanceMonitor.Enabled = true;
            NetworkMonitor.Enabled = true;
        }

        /// <summary>Unregisters a context. Thread-safe.</summary>
        /// <param name="context">The context to unregister.</param>
        public static void UnregisterContext(IContext context)
        {
            bool removed = false;
            lock (s_lock)
            {
                if (s_contextSet.Remove(context))
                {
                    s_activeContexts.Remove(context);
                    s_activeContextCount = s_activeContexts.Count;
                    s_activeContextsCacheDirty = true;
                    removed = true;
                }
            }
            if (removed)
            {
                try
                {
                    OnContextUnregistered?.Invoke(context);
                }
                catch (System.Exception ex)
                {
                    NexusLog.Error(nameof(NexusRuntime), nameof(UnregisterContext), string.Empty, ex);
                }
            }
        }
    }
}
