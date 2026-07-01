using System.Collections.Generic;
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

        /// <summary>Returns a thread-safe snapshot of all active contexts.</summary>
        /// <remarks>Locked access via <c>s_lock</c>. Returns a snapshot to prevent race conditions during iteration.</remarks>
        public static IReadOnlyList<IContext> ActiveContexts
        {
            get
            {
                lock (s_lock)
                {
                    return new List<IContext>(s_activeContexts);
                }
            }
        }

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

            if (context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnInitializeAsync(context.LifetimeToken);
                await lifecycle.OnStartAsync(context.LifetimeToken);
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
                        Debug.LogException(ex);
                    }
                }
                s_activeContexts.Clear();
                s_contextSet.Clear();
            }

            NexusDI.ClearCaches();
            Context.ClearAssemblyScanCache();
            SignalBus.ClearStaticCaches();
            Root.ClearRegistry();
            CommandPoolStatics.ClearStateLeakWarnings();
        }

        public static class Metrics
        {
            public static long TotalSignalsDispatched;
            public static long TotalCommandsExecuted;
            public static int ActiveContextCount => s_activeContexts.Count;

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
                System.Threading.Interlocked.Increment(ref TotalSignalsDispatched);
            }

            internal static void RecordCommandExecuted()
            {
                System.Threading.Interlocked.Increment(ref TotalCommandsExecuted);
            }

            // Production tracing ring buffer — always active, no NEXUS_DEBUG needed.
            // TracerPlugin reads this when causal tracing is compiled out.
            private const int TraceBufferSize = 200;
            private static readonly string[] s_traceBuffer = new string[TraceBufferSize];
            private static int s_traceIndex = -1;
            private static int s_traceCount;

            internal static void RecordTrace(string entry)
            {
                int idx = System.Threading.Interlocked.Increment(ref s_traceIndex) % TraceBufferSize;
                if (idx < 0) idx = 0;
                s_traceBuffer[idx] = entry;
                if (s_traceCount < TraceBufferSize)
                    System.Threading.Interlocked.Increment(ref s_traceCount);
            }

            public static string[] GetRecentTraces(out int count)
            {
                count = s_traceCount;
                if (count == 0) return System.Array.Empty<string>();
                var result = new string[count];
                int start = (s_traceIndex - count + 1 + TraceBufferSize) % TraceBufferSize;
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
                        long currSignals = System.Threading.Interlocked.Read(ref TotalSignalsDispatched);
                        long currCommands = System.Threading.Interlocked.Read(ref TotalCommandsExecuted);
                        s_signalsPerSecond = (currSignals - _prevSignals) / delta;
                        s_commandsPerSecond = (currCommands - _prevCommands) / delta;
                        System.Threading.Interlocked.Exchange(ref _prevSignals, currSignals);
                        System.Threading.Interlocked.Exchange(ref _prevCommands, currCommands);
                        _lastSampleTime = now;
                    }
                }
            }
        }

        /// <summary>Registers a context as active. Thread-safe.</summary>
        /// <param name="context">The context to register.</param>
        public static void RegisterContext(IContext context)
        {
            bool added = false;
            lock (s_lock)
            {
                if (s_contextSet.Add(context))
                {
                    s_activeContexts.Add(context);
                    added = true;
                }
            }
            if (added)
            {
                try
                {
                    OnContextRegistered?.Invoke(context);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
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
                    Debug.LogException(ex);
                }
            }
        }
    }
}
