using System;
using System.Collections.Generic;
using System.Threading;
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
    public interface IContextResolver
    {
        IReadOnlyList<IContext> GetActiveContexts();
    }

    public static class NexusRuntime
    {
        public static event System.Action<IContext> OnContextRegistered;
        public static event System.Action<IContext> OnContextUnregistered;
        public static IContextResolver DefaultContextResolver { get; } = new DefaultResolver();

        private sealed class DefaultResolver : IContextResolver
        {
            public IReadOnlyList<IContext> GetActiveContexts() => ActiveContexts;
        }

        private static readonly List<IContext> s_activeContexts = new();
        private static readonly HashSet<IContext> s_contextSet = new();
        private static readonly object s_lock = new();
        private static volatile int s_activeContextCount;
        private static List<IContext> s_activeContextsReadOnlyCache = new();
        private static bool s_activeContextsCacheDirty = true;
        // Audit fix 2.2: int + Interlocked so the first-registration monitoring init is
        // claimed atomically — two racing RegisterContext calls can no longer both pass
        // a plain check-then-set and double-initialize.
        private static int s_monitoringInitialized;

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

        /// <summary>Alias for <see cref="CurrentContext"/>.</summary>
        public static IContext GetDefaultContext() => CurrentContext;

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
            // M5 fix: no longer re-enters the ActiveContexts property getter (which takes
            // s_lock again). Monitor locks are reentrant on the same thread so this was not
            // a deadlock, but a nested acquisition inside an already-held lock is fragile:
            // if the cache-invalidation logic ever moves off-lock or the getter is changed
            // to a different lock, this silently becomes a deadlock or a stale read. Inline
            // the (dirty → rebuild) path under the single acquisition instead.
            lock (s_lock)
            {
                if (string.IsNullOrEmpty(scopeTag))
                {
                    if (s_activeContextsCacheDirty)
                    {
                        s_activeContextsReadOnlyCache = new List<IContext>(s_activeContexts);
                        s_activeContextsCacheDirty = false;
                    }
                    return s_activeContextsReadOnlyCache;
                }

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
        /// Cached per-context to avoid a DI lookup on every log call. Invalidated on context register/unregister.
        /// Returns null if no context or logger service is registered.
        /// </summary>
        public static Services.ILoggerService Logger
        {
            get
            {
                // Audit fix 2.1: the previous version held s_loggerCacheLock while calling
                // TryResolve, which acquires the DI container's _singletonLock — an AB-BA
                // deadlock pair against any DI path that logs while holding the container
                // lock (e.g. a constructor surfacing a recoverable error). The DI resolve
                // now runs lock-FREE; the cache publish is a single Interlocked CAS.
                // Side benefit (audit fix 5.1): the hot path is one volatile read — the
                // per-log-call lock + DI lookup is gone.
                var cached = System.Threading.Volatile.Read(ref s_cachedLogger);
                if (cached != null) return cached;

                var resolved = CurrentContext?.TryResolve<Services.ILoggerService>();
                if (resolved != null)
                    System.Threading.Interlocked.CompareExchange(ref s_cachedLogger, resolved, null);
                return resolved;
            }
        }

        private static Services.ILoggerService s_cachedLogger;
        // Retained for API compatibility with existing invalidation sites; the logger cache
        // no longer uses a lock (see Logger getter) — Volatile/Interlocked only.
        private static readonly object s_loggerCacheLock = new();

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
            await context.InitializeLifecycleAsync(context.ConfiguredLifecycles, context.LifetimeToken);

            return context;
        }

        /// <summary>
        /// Finalizes initialization across ALL registered contexts by running the PostContext
        /// lifecycle phase. Call this after all contexts have been created, configured, and
        /// initialized (OnConfigure → OnInitializeAsync → OnStartAsync).
        ///
        /// Each context's <see cref="IPostContextLifecycle.OnPostContext"/> is invoked in
        /// registration order, enabling cross-context wiring (StrangeIoC-style PostContexts).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public static async System.Threading.Tasks.Task FinalizeInitializationAsync(CancellationToken ct = default)
        {
            IReadOnlyList<IContext> snapshot;
            lock (s_lock)
            {
                // Use the current snapshot of all registered contexts
                snapshot = new List<IContext>(s_activeContexts);
            }

            // Honor ContextData.DependsOn: run PostContext in dependency order so a
            // dependent context's OnPostContext observes its dependencies fully wired.
            // Contexts without dependencies keep registration order (stable sort).
            var ordered = OrderContextsByDependencies(snapshot);

            for (int i = 0; i < ordered.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (ordered[i] is Context nexusCtx && nexusCtx.HasPostContextLifecycle)
                {
                    try
                    {
                        await nexusCtx.RunPostContextAsync(ct);
                    }
                    catch (System.Exception ex)
                    {
                        NexusLog.Error(nameof(NexusRuntime), nameof(FinalizeInitializationAsync),
                            $"PostContext failed for context '{nexusCtx.ScopeTag}'", ex);
                    }
                }
            }
        }

        /// <summary>
        /// Orders contexts so that dependencies listed in <see cref="ContextData.DependsOn"/> are
        /// processed before their dependents. Mirrors the editor-time matching rules (scope tag or
        /// asset name). Falls back to registration order when a cycle is detected.
        /// </summary>
        private static List<IContext> OrderContextsByDependencies(IReadOnlyList<IContext> contexts)
        {
            var byName = new Dictionary<string, IContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var ctx in contexts)
            {
                if (!string.IsNullOrEmpty(ctx.ScopeTag))
                    byName[ctx.ScopeTag] = ctx;
                var data = (ctx as Context)?.ContextData;
                if (data != null && !string.IsNullOrEmpty(data.name))
                    byName[data.name] = ctx;
            }

            var result = new List<IContext>(contexts.Count);
            var visited = new HashSet<IContext>();
            var visiting = new HashSet<IContext>();
            bool cycle = false;

            void Visit(IContext ctx)
            {
                if (cycle) return;
                if (!visiting.Add(ctx))
                {
                    cycle = true;
                    return;
                }
                if (!visited.Add(ctx))
                {
                    visiting.Remove(ctx);
                    return;
                }

                var data = (ctx as Context)?.ContextData;
                if (data?.DependsOn != null)
                {
                    for (int d = 0; d < data.DependsOn.Length; d++)
                    {
                        string dep = data.DependsOn[d];
                        if (string.IsNullOrEmpty(dep)) continue;
                        if (byName.TryGetValue(dep, out var depCtx) && depCtx != ctx)
                            Visit(depCtx);
                    }
                }

                visiting.Remove(ctx);
                result.Add(ctx);
            }

            for (int i = 0; i < contexts.Count; i++)
                Visit(contexts[i]);

            if (cycle)
            {
                NexusRuntime.Logger?.LogWarning(
                    "[Nexus] FinalizeInitializationAsync: ContextData.DependsOn contains a dependency cycle; falling back to registration order.");
                return new List<IContext>(contexts);
            }

            return result;
        }

        /// <summary>Disposes all active contexts and clears the registry. Called automatically on domain reload.</summary>
        public static void Reset()
        {
            // Audit fix 1.2: subscriber lists are captured and detached BEFORE the lock —
            // never nulled inside it. Previously a context Dispose (below) could synchronously
            // re-enter UnregisterContext while the registry state was still being torn down,
            // and a concurrent RegisterContext could attach a handler that was then silently
            // dropped when the in-lock null ran after the add. Detaching first also lets the
            // dispose loop below replay the unregistered notifications to the captured
            // subscribers, so listeners observe the same teardown they would have without Reset.
            var onUnregistered = OnContextUnregistered;
            OnContextRegistered = null;
            OnContextUnregistered = null;

            IContext[] snapshot;
            lock (s_lock)
            {
                snapshot = s_activeContexts.ToArray();
                s_activeContexts.Clear();
                s_contextSet.Clear();
                s_activeContextsReadOnlyCache = new List<IContext>();
                s_activeContextsCacheDirty = false;
                s_activeContextCount = 0;
                System.Threading.Volatile.Write(ref s_cachedLogger, null);
            }

            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                try
                {
                    snapshot[i].Dispose();
                }
                catch (System.Exception ex)
                {
                    NexusLog.Error(nameof(NexusRuntime), nameof(Reset), string.Empty, ex);
                }

                // Replay the unregistered notification outside the lock with the captured
                // delegate — handlers attached after Reset started are intentionally not
                // notified (they subscribed to the NEW registry generation).
                if (onUnregistered != null)
                {
                    try { onUnregistered(snapshot[i]); }
                    catch (System.Exception ex)
                    {
                        NexusLog.Error(nameof(NexusRuntime), nameof(Reset), string.Empty, ex);
                    }
                }
            }

            NexusDI.ClearCaches();
            Context.ClearAssemblyScanCache();
            Context.ClearDefaultScanAssembliesCache();
            // REFACTOR PLAN §1.2/§1.4/§2.3: shared convention/metadata caches join the reset
            // discipline — with Disable Domain Reload, statics persist across play sessions
            // and recompiles recreate Type instances while caches would hold stale ones.
            ContextBuilder.ClearCaches();
            ViewBinder.ClearCaches();
            SignalBus.ClearStaticCaches();
            QueuedSignalPoolRegistry.ClearAll();
            Root.ClearRegistry();
            CommandPoolStatics.ClearStateLeakWarnings();
            Services.AssemblyScanService.ClearCache();
            Metrics.ResetTraceBuffer();
            NexusLog.Reset();

            // R7 fix: clear static tamper events on domain reset so handlers do not
            // persist across disable-domain-reload sessions or editor play-mode cycles.
            try
            {
                // Nulling these events prevents stale subscriber leaks. Each SecureObservable
                // class exposes OnTamperDetected as a static event — clear them here via
                // the type-provided ClearOnTamperDetected helper to respect C# event access rules.
                SecureObservableInt.ClearOnTamperDetected();
                SecureObservableLong.ClearOnTamperDetected();
                SecureObservableFloat.ClearOnTamperDetected();
                SecureObservableString.ClearOnTamperDetected();
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Clearing tamper events during Reset failed: {ex.Message}");
            }
        }

        public static class Metrics
        {
            // Audit fix 4.1: master switch for the per-fire metrics. When disabled, a signal
            // fire pays a single volatile read instead of an Interlocked increment + a
            // ring-buffer slot write. Default TRUE — production tracing is a designed feature
            // (TracerPlugin reads the ring in release builds), so this is an opt-out escape
            // hatch for apps that measured the counter cost in their profiler, not a
            // behavior change.
            private static volatile bool s_metricsEnabled = true;
            public static bool MetricsEnabled
            {
                get => s_metricsEnabled;
                set => s_metricsEnabled = value;
            }

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
            // Capacity is configurable per-app via ContextData.TracerRingBufferSize;
            // each context applies its setting when it initializes (last one wins).
            private const int DefaultTraceBufferSize = 200;
            private const int MinTraceBufferSize = 64;
            // The buffer reference is the single source of truth: readers derive the
            // size from buffer.Length, so a reader can never observe a buffer/size
            // mismatch while ApplyTraceBufferSize swaps the array (the previous
            // separate s_traceBufferSize field could be read against a stale buffer,
            // indexing past the old array → IndexOutOfRangeException on the hot path).
            // Not declared volatile on purpose: every lock-free access goes through
            // Volatile.Read/Volatile.Write (declaring it volatile too would raise CS0420
            // on the ref-based Volatile calls). The in-lock reads/writes are serialized
            // by s_traceLock.
            private static string[] s_traceBuffer = new string[DefaultTraceBufferSize];
            private static int s_traceIndex = -1;
            private static int s_traceCount;
            private static readonly object s_traceLock = new();

            internal static void ApplyTraceBufferSize(int size)
            {
                if (size < MinTraceBufferSize) return;
                lock (s_traceLock)
                {
                    string[] current = s_traceBuffer;
                    if (size == current.Length) return;
                    var resized = new string[size];
                    int count = Math.Min(s_traceCount, size);
                    if (count > 0)
                    {
                        // Audit fix 2.3: Volatile.Read — RecordTrace advances s_traceIndex via
                        // Interlocked.Increment OUTSIDE s_traceLock, so a plain read here can
                        // observe a stale/torn value against the just-swapped buffer.
                        int oldIndex = System.Threading.Volatile.Read(ref s_traceIndex);
                        int start = ((oldIndex - count + 1) % current.Length + current.Length) % current.Length;
                        for (int i = 0; i < count; i++)
                            resized[i] = current[(start + i) % current.Length] ?? "";
                    }
                    // Single atomic publish point: after this volatile write the new
                    // array (and its Length) is what every lock-free reader sees.
                    System.Threading.Volatile.Write(ref s_traceBuffer, resized);
                    s_traceCount = count;
                    s_traceIndex = count - 1;
                }
            }

            internal static void ResetTraceBuffer()
            {
                lock (s_traceLock)
                {
                    // Volatile.Write for the same publish point as ApplyTraceBufferSize:
                    // lock-free readers observe the fresh array via Volatile.Read.
                    System.Threading.Volatile.Write(ref s_traceBuffer, new string[DefaultTraceBufferSize]);
                    s_traceIndex = -1;
                    s_traceCount = 0;
                }
            }

            internal static void RecordTrace(string entry)
            {
                string[] buffer = System.Threading.Volatile.Read(ref s_traceBuffer);
                // Size comes from the buffer itself, so buffer and size always agree
                // even when a concurrent ApplyTraceBufferSize swaps the array between
                // this read and the indexing below.
                int size = buffer.Length;
                int rawIndex = System.Threading.Interlocked.Increment(ref s_traceIndex);
                // Wrap-around-safe mapping: the unsigned cast yields a stable [0, size)
                // index for both positive and negative rawIndex (Interlocked.Increment
                // wraps to negative at 2^31), and it cannot overflow in checked mode
                // like -rawIndex could at int.MinValue.
                int idx = (int)((uint)rawIndex % (uint)size);
                buffer[idx] = entry;
                int currentCount = System.Threading.Volatile.Read(ref s_traceCount);
                if (currentCount < size)
                    System.Threading.Interlocked.CompareExchange(ref s_traceCount, currentCount + 1, currentCount);
            }

            public static string[] GetRecentTraces(out int count)
            {
                string[] buffer = System.Threading.Volatile.Read(ref s_traceBuffer);
                int size = buffer.Length;
                // Clamp the read count to the buffer we actually hold: a torn snapshot
                // taken while a resize swaps the array must not index past a stale
                // (smaller) buffer.
                count = Math.Min(System.Threading.Volatile.Read(ref s_traceCount), size);
                if (count == 0) return System.Array.Empty<string>();
                var result = new string[count];
                // Volatile read for consistency with the writers' Interlocked updates on
                // weak-memory (ARM) platforms — the rest of this block is equally careful.
                int lastIndex = System.Threading.Volatile.Read(ref s_traceIndex);
                // Audit fix 1.1: when the ring is full, the oldest entry sits at
                // (lastIndex + 1) % size — the slot the next write will overwrite. The old
                // formula (lastIndex - count + 1) returned the NEWEST slot (index 0 after the
                // first wraparound) as the oldest, silently dropping the freshest trace entry
                // from every TracerPlugin/dashboard read once the ring had wrapped.
                int start;
                if (count == size)
                {
                    start = (int)((uint)(lastIndex + 1) % (uint)size);
                }
                else
                {
                    // Ring not yet full: entries occupy [0, count) and lastIndex == count - 1.
                    // The negative-modulo guard keeps a torn read (count read before a
                    // concurrent writer advanced lastIndex) inside the valid range instead of
                    // indexing from a negative offset.
                    start = ((lastIndex - count + 1) % size + size) % size;
                }
                for (int i = 0; i < count; i++)
                    result[i] = buffer[(start + i) % size] ?? "";
                return result;
            }

            public static void UpdateRates()
            {
                float now = UnityEngine.Time.time;
                // Lock to make the rate calculation atomic across all fields.
                // Metrics are not a hot path so lock overhead is acceptable.
                lock (_rateLock)
                {
                    float lastSample = _lastSampleTime; // Read inside lock for atomicity
                    float delta = now - lastSample;
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
                    // Invalidate logger cache — a new context may provide a logger
                    System.Threading.Volatile.Write(ref s_cachedLogger, null);
                }
            }
            if (added)
            {
                // Initialize monitoring systems on first context registration.
                // Audit fix 2.2: Interlocked CAS — exactly ONE thread wins the claim even
                // when two contexts register concurrently; the plain check-then-set could
                // double-execute InitializeMonitoring.
                if (System.Threading.Interlocked.CompareExchange(ref s_monitoringInitialized, 1, 0) == 0)
                {
                    InitializeMonitoring();
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
                    s_activeContextsReadOnlyCache = new List<IContext>(s_activeContexts);
                    s_activeContextsCacheDirty = false;
                    removed = true;
                    // Invalidate logger cache — removed context may have been providing the logger
                    System.Threading.Volatile.Write(ref s_cachedLogger, null);
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
