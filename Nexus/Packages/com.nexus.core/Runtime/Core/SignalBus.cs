using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Unity.Profiling;
using Nexus.Core.Services;

namespace Nexus.Core
{
    /// <summary>
    /// Runs async work in a fire-and-forget manner. Uses async Task internally
    /// (not async void) so unhandled exceptions are caught by the Task infrastructure
    /// rather than crashing the process on the Unity SynchronizationContext.
    /// </summary>
    internal static class SafeAsyncRunner
    {
        public static void Run(Func<ValueTask> func, string errorContext)
        {
            _ = RunAsync(func, errorContext);
        }

        private static async Task RunAsync(Func<ValueTask> func, string errorContext)
        {
            try
            {
                await func().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SignalBus.RaiseUnhandledException(ex, errorContext);
                NexusRuntime.Logger?.LogError($"[Nexus] {errorContext}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    [Preserve]
    public partial class SignalBus : ISignalBus, IDisposable
    {
        // ─── Subscription management (linked list for zero-alloc sweep) ───
        // Linked-list yields O(n) unsubscribe/cleanup but keeps Subscribe allocation-free.
        // For large-scale (1000+) subscriber scenarios, prefer the command system.
        internal class SubscriptionNode
        {
            public object Handler;
            public object RawSubscription;
            public bool IsActive = true;
            public bool IsAsync;
            public SubscriptionNode Next;
            public void Reset() { Handler = null; RawSubscription = null; IsActive = true; IsAsync = false; Next = null; }
        }

        internal static class SubscriptionNodePool
        {
            private static readonly Stack<SubscriptionNode> s_pool = new();
            public static SubscriptionNode Rent(object handler, object rawSub, bool isAsync)
            {
                lock (s_pool)
                {
                    if (s_pool.Count > 0)
                    {
                        var node = s_pool.Pop();
                        node.Handler = handler; node.RawSubscription = rawSub;
                        node.IsActive = true; node.IsAsync = isAsync; node.Next = null;
                        return node;
                    }
                }
                return new SubscriptionNode { Handler = handler, RawSubscription = rawSub, IsAsync = isAsync };
            }
            public static void Return(SubscriptionNode node) { node.Reset(); lock (s_pool) { s_pool.Push(node); } }
            public static void Clear() { lock (s_pool) { s_pool.Clear(); } }
        }
        public static event Action<Exception, string> OnUnhandledException;

        internal static void RaiseUnhandledException(Exception ex, string context)
        {
            OnUnhandledException?.Invoke(ex, context);
        }

        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;
        private readonly IContextResolver _contextResolver;

        private readonly Dictionary<Type, List<CommandHandlerInfo>> _commandHandlers = new();
        private readonly Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersBySignal = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();
        private readonly object _handlerReadLock = new();

        // P0-3 fix: snapshots are cached behind a dirty flag so repeated property access
        // does not allocate a new Dictionary each time.
        private Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersSnapshot = new();
        private volatile Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersReadCopy = new();
        private Dictionary<Type, IReadOnlyList<CommandHandlerInfo>> _registeredHandlersSnapshot = new Dictionary<Type, IReadOnlyList<CommandHandlerInfo>>();
        private bool _handlersSnapshotDirty = true;

        public IReadOnlyDictionary<Type, List<CommandHandlerInfo>> CommandHandlers
        {
            get
            {
                lock (_handlerReadLock)
                {
                    RebuildHandlerSnapshotsIfDirty();
                    return _commandHandlersSnapshot;
                }
            }
        }

        /// <summary>
        /// Returns all registered signal→handler mappings.
        /// Populated by both fluent API (BindSignal/To) and attribute-based discovery.
        /// </summary>
        public IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers
        {
            get
            {
                lock (_handlerReadLock)
                {
                    RebuildHandlerSnapshotsIfDirty();
                    return _registeredHandlersSnapshot;
                }
            }
        }

        // Must be called while holding _handlerReadLock.
        private void RebuildHandlerSnapshotsIfDirty()
        {
            if (!_handlersSnapshotDirty && _commandHandlersSnapshot != null) return;
            _handlersSnapshotDirty = false;

            // Deep-copy the per-type handler lists so editor consumers never observe
            // concurrent mutation while a new handler is being registered.
            _commandHandlersSnapshot = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
            var dict = new Dictionary<Type, IReadOnlyList<CommandHandlerInfo>>(_commandHandlers.Count);
            foreach (var kvp in _commandHandlers)
            {
                var listCopy = new List<CommandHandlerInfo>(kvp.Value);
                _commandHandlersSnapshot[kvp.Key] = listCopy;
                dict[kvp.Key] = listCopy;
            }
            _registeredHandlersSnapshot = dict;
        }

        /// <summary>
        /// P0-3 fix: cached per-signal-type trace label so the trace ring buffer
        /// stays allocation-free on the hot path.
        /// </summary>
        private static class SignalTraceLabel<T> where T : struct
        {
            public static readonly string Fire = "▶ " + typeof(T).Name;
        }

        private readonly Dictionary<Type, SubscriptionNode> _subscriptions = new();
        private volatile Dictionary<Type, SubscriptionNode> _subscriptionsReadCopy = new();
        private readonly object _subLock = new();
        private readonly object _compositeLock = new();
        private bool _pendingCleanups;

        // Precomputed cache: does this signal type have at least one async handler?
        // Used by FireInternal to decide whether to delegate to the async path.
        private readonly Dictionary<Type, bool> _hasAsyncHandler = new();
        private volatile Dictionary<Type, bool> _hasAsyncHandlerReadCopy = new();

        private static readonly System.Threading.AsyncLocal<int> s_stackDepth = new();
        private const int MaxStackDepth = 10;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_signalSetterCache = new();

        // Cached dispatchers for generic-only commands (ICommand<TSignal>/IAsyncCommand<TSignal>)
        // used by the object-based fallback paths (recovery). Without these, a generic-only
        // fallback command would silently no-op because it is not a non-generic ICommand.
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_genericSyncDispatchCache = new();
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Func<object, object, CancellationToken, ValueTask>> s_genericAsyncDispatchCache = new();

        // Cached per-signal-type [CrossContext] attribute so the hot fire path never
        // performs an uncached reflection GetCustomAttribute call per signal dispatch.
        private static readonly ConcurrentDictionary<Type, CrossContextAttribute> s_crossContextCache = new();

        private static CrossContextAttribute GetCachedCrossContext(Type type)
            => s_crossContextCache.GetOrAdd(type, static t => t.GetCustomAttribute<CrossContextAttribute>());

        private static readonly Stack<List<object>> s_listPool = new();
        private static List<object> GetPooledList()
        {
            lock (s_listPool)
            {
                return s_listPool.Count > 0 ? s_listPool.Pop() : new List<object>();
            }
        }
        private static void ReturnPooledList(List<object> list)
        {
            list.Clear();
            lock (s_listPool)
            {
                s_listPool.Push(list);
            }
        }

#if NEXUS_DEBUG
        private static readonly ProfilerMarker s_DispatchMarker = new ProfilerMarker("Nexus.Signal.Dispatch");
        private static readonly ProfilerMarker s_CommandMarker = new ProfilerMarker("Nexus.Command.Execute");
        private static readonly ProfilerMarker s_DrainMarker = new ProfilerMarker("Nexus.Queue.Drain");
#endif

        public SignalBus(NexusDI container, CommandPoolManager poolManager, IContext context)
            : this(container, poolManager, context, null)
        {
        }

        public SignalBus(NexusDI container, CommandPoolManager poolManager, IContext context, IContextResolver contextResolver)
        {
            _container = container;
            _poolManager = poolManager;
            _context = context;
            _contextResolver = contextResolver ?? NexusRuntime.DefaultContextResolver;
        }

        internal static bool ImplementsGenericInterface(Type type, Type genericInterface)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == genericInterface)
                    return true;
            }
            return false;
        }

        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync)
        {
            var genericSyncType = typeof(ICommand<>).MakeGenericType(signalType);
            var genericAsyncType = typeof(IAsyncCommand<>).MakeGenericType(signalType);
            bool implementsGenericSync = genericSyncType.IsAssignableFrom(commandType);
            bool implementsGenericAsync = genericAsyncType.IsAssignableFrom(commandType);

            if (!implementsGenericSync && !implementsGenericAsync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} registered for signal {signalType.Name} must implement either ICommand<{signalType.Name}> or IAsyncCommand<{signalType.Name}>.");
            }

            if (implementsGenericAsync && implementsGenericSync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} cannot implement both ICommand and IAsyncCommand interfaces.");
            }
            if (implementsGenericAsync && !isAsync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} implements IAsyncCommand but is being registered as sync. It must be registered as async (isAsync: true).");
            }

            // P0-5 fix: honor [CommandTimeout] at registration time.
            var timeoutAttr = commandType.GetCustomAttribute<CommandTimeoutAttribute>();
            int timeoutMs = timeoutAttr != null ? timeoutAttr.Milliseconds : 0;

            // P1-2 fix: all handler-table mutations happen under _handlerReadLock,
            // and lock-free readers get rebuilt volatile snapshots.
            lock (_handlerReadLock)
            {
                if (!_commandHandlers.TryGetValue(signalType, out var list))
                {
                    list = new List<CommandHandlerInfo>();
                    _commandHandlers[signalType] = list;
                }

                // Verify Mixed-Mode restriction
                if (list.Count > 0 && list[0].Mode != mode)
                {
                    throw new InvalidOperationException($"Mixed-mode dispatch error: Signal {signalType.Name} already registered with mode {list[0].Mode}, cannot add handler with mode {mode}.");
                }

                // Verify Exclusive mode restriction
                if (mode == ExecutionMode.Exclusive && list.Count > 0)
                {
                    throw new InvalidOperationException($"Exclusive execution mode violation: Signal {signalType.Name} already has a handler registered.");
                }

                // Verify priority uniqueness for Sequential/Exclusive
                if (mode != ExecutionMode.Concurrent)
                {
                    foreach (var handler in list)
                    {
                        if (handler.Priority == priority)
                        {
                            // Priority tie break fallback check or Build/Validation error
                            throw new InvalidOperationException($"Duplicate priority {priority} for signal {signalType.Name}.");
                        }
                    }
                }

                list.Add(new CommandHandlerInfo(commandType, mode, priority, isAsync, timeoutMs));
                _handlersSnapshotDirty = true;

                // Update async handler cache — if any handler is async, mark the signal type
                if (isAsync)
                {
                    _hasAsyncHandler[signalType] = true;
                }
                else if (!_hasAsyncHandler.ContainsKey(signalType))
                {
                    _hasAsyncHandler[signalType] = false;
                }

                // Sort by priority descending (higher priority runs first)
                if (mode != ExecutionMode.Concurrent)
                {
                    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }

                // Deep-copy each list so concurrent dispatch iterates an immutable snapshot.
                _commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
                foreach (var kvp in _commandHandlers)
                    _commandHandlersReadCopy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
                _hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
            }

            // Bind command type in DI so CommandPoolManager can resolve it
            _container.Bind(commandType, isSingleton: false);
        }

        public void RegisterCompositeCommand(Type[] signalTypes, Type commandType, bool oneShot, int priority, bool isAsync)
        {
            if (signalTypes == null || signalTypes.Length == 0)
                throw new ArgumentException("Composite command requires at least one signal type.", nameof(signalTypes));
            if (signalTypes.Length > 64 || signalTypes.Length == 0)
                throw new ArgumentException($"Composite command requires between 1 and 64 signal types. Received {signalTypes.Length}.", nameof(signalTypes));

            // A duplicate signal type would set the same bit twice; the trigger could never
            // reach its TargetMask (or would fire with an ambiguous payload), so reject it.
            for (int i = 0; i < signalTypes.Length; i++)
            {
                if (signalTypes[i] == null)
                    throw new ArgumentException("Composite signal types cannot be null.", nameof(signalTypes));
                for (int j = i + 1; j < signalTypes.Length; j++)
                {
                    if (signalTypes[i] == signalTypes[j])
                    {
                        throw new ArgumentException(
                            $"Composite command requires unique signal types; '{signalTypes[i].Name}' appears more than once.",
                            nameof(signalTypes));
                    }
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Composite commands cannot use the single-signal generic command interfaces
            // (ICommand<T>/IAsyncCommand<T>) since a composite spans multiple signal types.
            // Guide the user toward ICompositeCommand/IAsyncCompositeCommand for payload access.
            if (ImplementsGenericInterface(commandType, typeof(ICommand<>)) || ImplementsGenericInterface(commandType, typeof(IAsyncCommand<>)))
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Composite command '{commandType.Name}' implements a single-signal generic command interface (ICommand<T>/IAsyncCommand<T>), which is not supported for composites. Implement ICompositeCommand / IAsyncCompositeCommand to receive all trigger payloads, or non-generic ICommand / IAsyncCommand if no payload is needed.");
            }
#endif

            var state = new CompositeTriggerState(commandType, signalTypes, oneShot, priority);

            // P1-2 fix: mutate composite tables under _compositeLock (same lock the
            // dispatch path uses) so registration cannot race ProcessCompositeTriggers.
            lock (_compositeLock)
            {
                _allCompositeTriggers.Add(state);

                foreach (var sigType in signalTypes)
                {
                    if (!_compositeTriggersBySignal.TryGetValue(sigType, out var list))
                    {
                        list = new List<CompositeTriggerState>();
                        _compositeTriggersBySignal[sigType] = list;
                    }
                    list.Add(state);
                    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }
            }

            _container.Bind(commandType, isSingleton: false);
        }

        public void Fire<T>(T signal) where T : struct
        {
            FireInternal(signal, isCrossContextSource: false);
        }

        public async ValueTask FireAsync<T>(T signal) where T : struct
        {
            await FireInternalAsync(signal, isCrossContextSource: false);
        }

        public void FireThreadSafe<T>(T signal) where T : struct
        {
            var hybridQueue = _container.Resolve<HybridQueue>();
            hybridQueue.EnqueueThreadSafe(signal);
        }

        public void FireNextFrame<T>(T signal) where T : struct
        {
            var hybridQueue = _container.Resolve<HybridQueue>();
            hybridQueue.EnqueueNextFrame(signal);
        }

        public async ValueTask FireAsyncWithTimeout<T>(T signal, int timeoutMilliseconds) where T : struct
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_context.LifetimeToken);
            timeoutCts.CancelAfter(timeoutMilliseconds);
            try
            {
                await FireInternalAsync(signal, isCrossContextSource: false, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!_context.LifetimeToken.IsCancellationRequested)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Async signal '{typeof(T).Name}' timed out after {timeoutMilliseconds}ms.");
                throw;
            }
        }

        public async ValueTask FireAsyncAndForget<T>(T signal, Action<Exception> onError = null) where T : struct
        {
            try
            {
                await FireInternalAsync(signal, isCrossContextSource: false);
            }
            catch (OperationCanceledException)
            {
                // Expected during context teardown; nothing to surface (and no unobserved task).
            }
            catch (Exception ex)
            {
                if (onError != null)
                {
                    onError(ex);
                }
                else
                {
                    OnUnhandledException?.Invoke(ex, $"FireAsyncAndForget failed for signal '{typeof(T).FullName}'");
                    NexusRuntime.Logger?.LogError($"[Nexus] FireAsyncAndForget signal '{typeof(T).Name}' failed: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            SignalSubscription<T> sub = null;
            sub = new SignalSubscription<T>(handler, _context.LifetimeToken, () => Unsubscribe(type, sub));

            lock (_subLock)
            {
                _subscriptions.TryGetValue(type, out var head);
                var node = SubscriptionNodePool.Rent(handler, sub, isAsync: false);
                node.Next = head;
                _subscriptions[type] = node;
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);
            }
            return sub;
        }

        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            var type = typeof(T);
            AsyncSignalSubscription<T> sub = null;
            sub = new AsyncSignalSubscription<T>(handler, _context.LifetimeToken, () => Unsubscribe(type, sub));

            lock (_subLock)
            {
                _subscriptions.TryGetValue(type, out var head);
                var node = SubscriptionNodePool.Rent(handler, sub, isAsync: true);
                node.Next = head;
                _subscriptions[type] = node;
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);
            }
            return sub;
        }

        private void Unsubscribe(Type type, object rawSub)
        {
            lock (_subLock)
            {
                if (_subscriptions.TryGetValue(type, out var current))
                {
                    while (current != null)
                    {
                        if (current.RawSubscription == rawSub)
                        {
                            current.IsActive = false;
                            _pendingCleanups = true;
                            break;
                        }
                        current = current.Next;
                    }
                }
            }

            // Free dead nodes immediately when nothing is dispatching (deferred to the next
            // fire's finally otherwise, which could otherwise retain handlers until then).
            if (s_stackDepth.Value == 0)
            {
                SweepDeadNodes();
            }
        }

        // P0-3 fix: reusable key buffer so SweepDeadNodes does not allocate per sweep.
        private readonly List<Type> _sweepKeysCache = new();

        private void SweepDeadNodes()
        {
            lock (_subLock)
            {
                if (!_pendingCleanups) return;
                _pendingCleanups = false;

                var keys = _sweepKeysCache;
                keys.Clear();
                foreach (var key in _subscriptions.Keys)
                {
                    keys.Add(key);
                }

                foreach (var type in keys)
                {
                    if (_subscriptions.TryGetValue(type, out var current))
                    {
                        SubscriptionNode prev = null;
                        while (current != null)
                        {
                            if (!current.IsActive)
                            {
                                var next = current.Next;
                                if (prev == null)
                                {
                                    if (next == null)
                                        _subscriptions.Remove(type);
                                    else
                                        _subscriptions[type] = next;
                                }
                                else
                                {
                                    prev.Next = next;
                                }
                                var temp = current;
                                current = next;
                                SubscriptionNodePool.Return(temp);
                            }
                            else
                            {
                                prev = current;
                                current = current.Next;
                            }
                        }
                    }
                }

                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>(_subscriptions);
            }
        }

        private void FireInternal<T>(T signal, bool isCrossContextSource) where T : struct
        {
            var type = typeof(T);

            NexusRuntime.Metrics.RecordSignalDispatched();
            NexusRuntime.Metrics.RecordTrace(SignalTraceLabel<T>.Fire);

            // Plan §1.4.1 — If this signal has ANY async handlers registered,
            // delegate to the async path to preserve Sequential ordering guarantees.
            // The async path properly awaits each handler in priority order.
            // Sync-only signals take the fast path below with zero async overhead.
            // P1-2 fix: reads go through volatile snapshots (no unsynchronized Dictionary access).
            bool hasAsync = _hasAsyncHandlerReadCopy.TryGetValue(type, out var asyncFlag) && asyncFlag;
            bool hasAsyncSubscriptions = HasAsyncSubscriptions(type);

            if (hasAsync || hasAsyncSubscriptions)
            {
                throw new NexusSyncAsyncMismatchException(
                    $"Synchronous Fire() was called for signal '{typeof(T).FullName}', but it has asynchronous handlers or subscriptions registered. " +
                    "To preserve sequential ordering and prevent race conditions, you must invoke this signal using FireAsync() and await its completion, or use FireAsyncAndForget().");
            }

            // === FAST PATH: All handlers are synchronous ===
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_subscriptionsReadCopy.ContainsKey(type) && !_commandHandlersReadCopy.ContainsKey(type))
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Signal '{typeof(T).FullName}' fired but has no subscribers or command handlers registered. This may indicate a missing BindCommand or Subscribe call.");
            }
#endif
            s_stackDepth.Value++;
            if (s_stackDepth.Value > MaxStackDepth)
            {
                // P0-7 fix: never reset the counter to 0 (outer frames still decrement in
                // their finally blocks, which would drift the counter negative). This branch
                // runs before this frame's try/finally, so undo only this frame's increment.
                s_stackDepth.Value--;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
#else
                NexusRuntime.Logger?.LogError($"[Nexus] Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
                return;
#endif
            }

#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
            s_DispatchMarker.Begin();
#endif
            try
            {
                // Run plugins' SignalInterceptors
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.HasInterceptors)
                {
                    object boxedSignal = signal;
                    var plugins = ctx.PluginsReadOnlyCopy;
                    for (int i = 0; i < plugins.Count; i++)
                    {
                        var interceptors = plugins[i].context.Interceptors;
                        for (int j = 0; j < interceptors.Count; j++)
                        {
                            if (!interceptors[j].Intercept(ref boxedSignal))
                            {
                                interceptorCancelled = true;
                                break;
                            }
                        }
                        if (interceptorCancelled) break;
                    }
                    signal = (T)boxedSignal;
                }

                if (interceptorCancelled)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(eventId, TraceStatus.Cancelled);
#endif
                    return;
                }

                // Handle Cross-Context
                if (!isCrossContextSource)
                {
                    var crossContextAttr = GetCachedCrossContext(type);
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // ═══ EXECUTION ORDER GUARANTEE ═══
                // Commands execute FIRST (they mutate model state),
                // then subscriptions execute AFTER (they observe final state).
                // This ensures mediators/views always read post-command state.

                // Phase 1: Process commands (mutate state)
                if (_commandHandlersReadCopy.TryGetValue(type, out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        ExecuteCommand(handler, signal);
                    }
                }

                // Phase 2: Process subscriptions (observe final state)
                if (_subscriptionsReadCopy.TryGetValue(type, out var node))
                {
                    var current = node;
                    while (current != null)
                    {
                        if (current.IsActive && current.Handler is Action<T> syncSub)
                        {
                            syncSub(signal);
                        }
                        current = current.Next;
                    }
                }

                // Process composite triggers
                ProcessCompositeTriggers(signal);
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
#endif
            }
            catch (Exception ex)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
#endif
                // Collect error information (don't log to console for expected exceptions)
                bool shouldLog = !(ex is NexusReentrancyException || ex is NexusAsyncOverflowException || ex is OperationCanceledException)
                    && !(ex is InvalidOperationException ioe && ioe.Message.Contains("Execution aborted"));
                ErrorCollection.CollectException(ex, ErrorCollection.ErrorCategory.Signal, 
                    $"Signal dispatch failed for {typeof(T).FullName}", shouldLog);
                throw;
            }
            finally
            {
#if NEXUS_DEBUG
                s_DispatchMarker.End();
#endif
                s_stackDepth.Value--;
                if (s_stackDepth.Value == 0 && _pendingCleanups)
                {
                    SweepDeadNodes();
                }
            }
        }

        /// <summary>
        /// P0-4 fix: async-safe dispatch for recovery signals. If the failed-command
        /// signal has async handlers/subscriptions, route it through the async path
        /// (fire-and-forget with error capture) instead of throwing
        /// <see cref="NexusSyncAsyncMismatchException"/> during error handling.
        /// FireAsyncAndForget already catches and logs all exceptions internally
        /// (see its catch blocks for OperationCanceledException and Exception).
        /// The _ = discard is intentional — the async path handles its own errors.
        /// </summary>
        private void FireFailedSignalSafe(CommandFailedSignal failedSignal)
        {
            bool hasAsync = (_hasAsyncHandlerReadCopy.TryGetValue(typeof(CommandFailedSignal), out var flag) && flag)
                || HasAsyncSubscriptions(typeof(CommandFailedSignal));
            if (hasAsync)
            {
                SafeAsyncRunner.Run(() => FireInternalAsync(failedSignal, isCrossContextSource: false),
                    "CommandFailedSignal async dispatch failed");
            }
            else
            {
                _ = FireAsyncAndForget(failedSignal);
            }
        }

        /// <summary>
        /// Checks if a signal type has any async subscriptions (SubscribeAsync).
        /// </summary>
        private bool HasAsyncSubscriptions(Type signalType)
        {
            if (!_subscriptionsReadCopy.TryGetValue(signalType, out var node))
                return false;

            var current = node;
            while (current != null)
            {
                if (current.IsActive && current.IsAsync)
                    return true;
                current = current.Next;
            }
            return false;
        }

        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            await FireInternalAsync(signal, isCrossContextSource, _context.LifetimeToken);
        }

        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource, CancellationToken ct) where T : struct
        {
            s_stackDepth.Value++;

            // Capture the command-scoped token for use in the nested scopes below.
            // This allows FireAsyncWithTimeout to cancel command execution via a linked token.
            var commandCt = ct;
            if (s_stackDepth.Value > MaxStackDepth)
            {
                // P0-7 fix: never reset the counter to 0 (outer frames still decrement in
                // their finally blocks, which would drift the counter negative). This branch
                // runs before this frame's try/finally, so undo only this frame's increment.
                s_stackDepth.Value--;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
#else
                NexusRuntime.Logger?.LogError($"[Nexus] Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
                return;
#endif
            }

#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
#endif
            try
            {
                var type = typeof(T);

                // Run plugins' SignalInterceptors
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.HasInterceptors)
                {
                    object boxedSignal = signal;
                    var plugins = ctx.PluginsReadOnlyCopy;
                    for (int i = 0; i < plugins.Count; i++)
                    {
                        var interceptors = plugins[i].context.Interceptors;
                        for (int j = 0; j < interceptors.Count; j++)
                        {
                            if (!interceptors[j].Intercept(ref boxedSignal))
                            {
                                interceptorCancelled = true;
                                break;
                            }
                        }
                        if (interceptorCancelled) break;
                    }
                    signal = (T)boxedSignal;
                }

                if (interceptorCancelled)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(eventId, TraceStatus.Cancelled);
#endif
                    return;
                }

                // Handle Cross-Context
                if (!isCrossContextSource)
                {
                    var crossContextAttr = GetCachedCrossContext(type);
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // ═══ EXECUTION ORDER GUARANTEE (Async Path) ═══
                // Commands execute FIRST (they mutate model state),
                // then subscriptions execute AFTER (they observe final state).

                // Phase 1: Process commands (mutate state)
                if (_commandHandlersReadCopy.TryGetValue(type, out var handlers))
                {
                    if (handlers.Count > 0 && handlers[0].Mode == ExecutionMode.Concurrent)
                    {
                        // Run concurrently
                        int taskCount = handlers.Count;
                        var tasks = System.Buffers.ArrayPool<ValueTask>.Shared.Rent(taskCount);
                        try
                        {
                            for (int i = 0; i < taskCount; i++)
                            {
                                tasks[i] = ExecuteCommandAsync(handlers[i], signal, commandCt);
                            }
                            
                            for (int i = 0; i < taskCount; i++)
                            {
                                await tasks[i];
                            }
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<ValueTask>.Shared.Return(tasks);
                        }
                    }
                    else
                    {
                        // Run sequentially
                        foreach (var handler in handlers)
                        {
                            if (handler.IsAsync)
                            {
                                await ExecuteCommandAsync(handler, signal, commandCt);
                            }
                            else
                            {
                                ExecuteCommand(handler, signal);
                            }
                        }
                    }
                }

                // Phase 2: Process subscriptions (observe final state)
                if (_subscriptionsReadCopy.TryGetValue(type, out var node))
                {
                    var current = node;
                    while (current != null)
                    {
                        if (current.IsActive)
                        {
                            var handler = current.Handler;
                            if (handler is Action<T> syncSub)
                            {
                                syncSub(signal);
                            }
                            else if (handler is Func<T, CancellationToken, ValueTask> asyncSub)
                            {
                                // P2-12 fix: pass the command-scoped token so subscriptions
                                // also honour the FireAsyncWithTimeout timeout.
                                await asyncSub(signal, commandCt);
                            }
                        }
                        current = current.Next;
                    }
                }

                // Process composite triggers
                ProcessCompositeTriggers(signal);
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
#endif
            }
            catch (Exception ex)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
#endif
                // Collect error information (don't log to console for expected exceptions)
                bool shouldLog = !(ex is NexusReentrancyException || ex is NexusAsyncOverflowException || ex is OperationCanceledException)
                    && !(ex is InvalidOperationException ioe && ioe.Message.Contains("Execution aborted"));
                ErrorCollection.CollectException(ex, ErrorCollection.ErrorCategory.Signal, 
                    $"Signal dispatch failed for {typeof(T).FullName}", shouldLog);
                throw;
            }
            finally
            {
                s_stackDepth.Value--;
                if (s_stackDepth.Value == 0 && _pendingCleanups)
                {
                    SweepDeadNodes();
                }
            }
        }



        private void ExecuteCommand<TSignal>(CommandHandlerInfo handler, TSignal signal) where TSignal : struct
        {
            int retryCount = 0;
            bool shouldRun = true;

            NexusRuntime.Metrics.RecordCommandExecuted();
            NexusRuntime.Metrics.RecordTrace(handler.TraceLabel);

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                try
                {
                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    
                    if (command is ICommand<TSignal> genericSyncCmd)
                    {
                        // P0-3 fix: bypass closure allocation when no decorators are registered.
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
                            ExecuteWithDecorators(genericSyncCmd, () => genericSyncCmd.Execute(signal));
                        }
                        else
                        {
                            genericSyncCmd.Execute(signal);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Command '{handler.CommandType.Name}' registered for signal '{typeof(TSignal).Name}' must implement ICommand<{typeof(TSignal).Name}>.");
                    }
                    shouldRun = false; // completed successfully
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = HandleCommandErrorWithDecision(ex, handler.CommandType, signal, ref retryCount);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }

        private void ExecuteCommand(CommandHandlerInfo handler, object signal)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                try
                {
                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    InjectSignal(command, signal);

                    if (command is ICommand syncCmd)
                    {
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
                        else
                        {
                            syncCmd.Execute();
                        }
                    }
                    else if (signal != null)
                    {
                        // Generic-only command (ICommand<TSignal>): dispatch via cached reflection.
                        // Previously this silently no-oped because the command was not ICommand.
                        var dispatcher = GetGenericSyncDispatcher(command.GetType(), signal.GetType());
                        if (dispatcher == null)
                        {
                            throw new InvalidOperationException($"Command '{handler.CommandType.Name}' must implement ICommand or ICommand<{signal.GetType().Name}>.");
                        }
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
                            ExecuteWithDecorators(command, () => dispatcher(command, signal));
                        }
                        else
                        {
                            dispatcher(command, signal);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Command '{handler.CommandType.Name}' must implement ICommand.");
                    }
                    shouldRun = false; // completed successfully
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = HandleCommandErrorWithDecision(ex, handler.CommandType, signal, ref retryCount);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }



        private async ValueTask ExecuteCommandAsync<TSignal>(CommandHandlerInfo handler, TSignal signal, CancellationToken ct) where TSignal : struct
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                bool inFlightIncremented = false;
                try
                {
                    var count = Interlocked.Increment(ref _inFlightAsyncCommands);
                    if (count > MaxInFlightAsyncCommands)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        throw new NexusAsyncOverflowException($"Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
#else
                        NexusRuntime.Logger?.LogError($"[Nexus] Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
                        shouldRun = false;
                        break;
#endif
                    }
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
 
                    if (command is IAsyncCommand<TSignal> genericAsyncCmd)
                    {
                        // P0-5 fix: apply [CommandTimeout] via a linked, self-cancelling token.
                        if (handler.TimeoutMs > 0)
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeoutCts.CancelAfter(handler.TimeoutMs);
                            var timeoutToken = timeoutCts.Token;
                            await ExecuteWithDecoratorsAsync(genericAsyncCmd, async () => await genericAsyncCmd.ExecuteAsync(signal, timeoutToken));
                        }
                        else
                        {
                            await ExecuteWithDecoratorsAsync(genericAsyncCmd, async () => await genericAsyncCmd.ExecuteAsync(signal, ct));
                        }
                    }
                    else if (command is ICommand<TSignal> genericSyncCmd)
                    {
                        ExecuteWithDecorators(genericSyncCmd, () => genericSyncCmd.Execute(signal));
                    }
                    else
                    {
                        throw new InvalidOperationException($"Command '{handler.CommandType.Name}' registered for signal '{typeof(TSignal).Name}' must implement IAsyncCommand<{typeof(TSignal).Name}> or ICommand<{typeof(TSignal).Name}>.");
                    }
                    shouldRun = false; // success
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = await HandleCommandErrorWithDecisionAsync(ex, handler.CommandType, signal, retryCount, ct);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (inFlightIncremented)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
                    }
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }

        private async ValueTask ExecuteCommandAsync(CommandHandlerInfo handler, object signal, CancellationToken ct)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                bool inFlightIncremented = false;
                try
                {
                    var count = Interlocked.Increment(ref _inFlightAsyncCommands);
                    if (count > MaxInFlightAsyncCommands)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        throw new NexusAsyncOverflowException($"Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
#else
                        NexusRuntime.Logger?.LogError($"[Nexus] Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
                        shouldRun = false;
                        break;
#endif
                    }
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    InjectSignal(command, signal);

                    if (command is IAsyncCommand asyncCmd)
                    {
                        // P0-5 fix: apply [CommandTimeout] via a linked, self-cancelling token.
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
                            if (handler.TimeoutMs > 0)
                            {
                                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                timeoutCts.CancelAfter(handler.TimeoutMs);
                                var timeoutToken = timeoutCts.Token;
                                await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(timeoutToken));
                            }
                            else
                            {
                                await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
                            }
                        }
                        else
                        {
                            if (handler.TimeoutMs > 0)
                            {
                                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                timeoutCts.CancelAfter(handler.TimeoutMs);
                                await asyncCmd.ExecuteAsync(timeoutCts.Token);
                            }
                            else
                            {
                                await asyncCmd.ExecuteAsync(ct);
                            }
                        }
                    }
                    else if (command is ICommand syncCmd)
                    {
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
                        else
                        {
                            syncCmd.Execute();
                        }
                    }
                    else if (signal != null)
                    {
                        // Generic-only command: prefer IAsyncCommand<TSignal>, then ICommand<TSignal>.
                        // Previously a generic-only fallback command silently no-oped here.
                        var asyncDispatcher = GetGenericAsyncDispatcher(command.GetType(), signal.GetType());
                        if (asyncDispatcher != null)
                        {
                            if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                            {
                                if (handler.TimeoutMs > 0)
                                {
                                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                    timeoutCts.CancelAfter(handler.TimeoutMs);
                                    var timeoutToken = timeoutCts.Token;
                                    await ExecuteWithDecoratorsAsync(command, async () => await asyncDispatcher(command, signal, timeoutToken));
                                }
                                else
                                {
                                    await ExecuteWithDecoratorsAsync(command, async () => await asyncDispatcher(command, signal, ct));
                                }
                            }
                            else
                            {
                                if (handler.TimeoutMs > 0)
                                {
                                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                    timeoutCts.CancelAfter(handler.TimeoutMs);
                                    await asyncDispatcher(command, signal, timeoutCts.Token);
                                }
                                else
                                {
                                    await asyncDispatcher(command, signal, ct);
                                }
                            }
                        }
                        else
                        {
                            var syncDispatcher = GetGenericSyncDispatcher(command.GetType(), signal.GetType());
                            if (syncDispatcher == null)
                            {
                                throw new InvalidOperationException($"Command '{handler.CommandType.Name}' must implement IAsyncCommand, IAsyncCommand<TSignal>, ICommand, or ICommand<{signal.GetType().Name}>.");
                            }
                            if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                            {
                                ExecuteWithDecorators(command, () => syncDispatcher(command, signal));
                            }
                            else
                            {
                                syncDispatcher(command, signal);
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Command '{handler.CommandType.Name}' must implement IAsyncCommand or ICommand.");
                    }
                    shouldRun = false; // success
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = await HandleCommandErrorWithDecisionAsync(ex, handler.CommandType, signal, retryCount, ct);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (inFlightIncremented)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
                    }
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }

        /// <summary>
        /// InjectSignal assigns the signal payload to the matching field or property on the command instance.
        /// This is only used in non-generic ICommand execution.
        /// Performs reflection once per (commandType, signalType) pair, then caches the compiled setter
        /// delegate in a thread-safe dictionary to avoid reflection overhead on subsequent dispatches.
        /// </summary>
        private void InjectSignal(object command, object signal)
        {
            if (signal == null) return;

            var commandType = command.GetType();
            var signalType = signal.GetType();
            var cacheKey = (commandType, signalType);

            var setter = s_signalSetterCache.GetOrAdd(cacheKey, key =>
            {
                var cmdType = key.commandType;
                var sigType = key.signalType;
                Action<object, object> newSetter = null;
                MemberInfo foundMember = null;

                // Match fields by exact type OR name convention (e.g. _signal or signal)
                var fields = cmdType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    if (field.FieldType == sigType || 
                        (field.FieldType.IsInstanceOfType(signal) && 
                         (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) || 
                          field.Name.Equals("signal", StringComparison.OrdinalIgnoreCase))))
                    {
                        foundMember = field;
                        break;
                    }
                }

                if (foundMember == null)
                {
                    var properties = cmdType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var prop in properties)
                    {
                        if (prop.PropertyType == sigType && prop.CanWrite)
                        {
                            foundMember = prop;
                            break;
                        }
                    }
                }

                if (foundMember != null)
                {
                    if (foundMember is FieldInfo f)
                        newSetter = (target, val) => f.SetValue(target, val);
                    else if (foundMember is PropertyInfo p)
                        newSetter = (target, val) => p.SetValue(target, val);
                }
                else
                {
                    newSetter = (target, val) => { };
                }
                return newSetter;
            });

            setter(command, signal);
        }

        private void ProcessCompositeTriggers<T>(T signal) where T : struct
        {
            // P1-14 fix: collect due triggers under _compositeLock, then execute them
            // OUTSIDE the lock so user command code never runs while holding it.
            var signalType = typeof(T);
            List<(CompositeTriggerState trigger, CompositeContext context)> dueTriggers = null;
            // Composite payload support: box the signal at most once, and only when it actually
            // feeds a registered composite trigger. Non-composite signals never allocate here.
            object boxedSignal = null;

            lock (_compositeLock)
            {
                if (!_compositeTriggersBySignal.TryGetValue(signalType, out var triggers))
                    return;

                foreach (var trigger in triggers)
                {
                    if (trigger.IsCompleted) continue;

                    int index = Array.IndexOf(trigger.RequiredSignals, signalType);
                    if (index >= 0)
                    {
                        boxedSignal ??= signal;
                        trigger.CapturePayload(index, boxedSignal);
                        trigger.CurrentMask |= (1UL << index);

                        if (trigger.CurrentMask == trigger.TargetMask)
                        {
                            // Snapshot payloads INSIDE the lock so a concurrent fire that resets a
                            // repeatable trigger cannot corrupt the context handed to the command.
                            var context = new CompositeContext(trigger.RequiredSignals, trigger.SnapshotPayloads());
                            dueTriggers ??= new List<(CompositeTriggerState, CompositeContext)>();
                            dueTriggers.Add((trigger, context));

                            if (trigger.OneShot)
                            {
                                trigger.IsCompleted = true;
                            }
                            else
                            {
                                trigger.CurrentMask = 0;
                                trigger.ClearPayloads();
                            }
                        }
                    }
                }
            }

            if (dueTriggers != null)
            {
                for (int i = 0; i < dueTriggers.Count; i++)
                {
                    ExecuteCompositeCommand(dueTriggers[i].trigger, dueTriggers[i].context);
                }
            }
        }

        private async ValueTask ExecuteCompositeCommandAsyncCore(CompositeTriggerState trigger, object command, CompositeContext context)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
                bool inFlightIncremented = false;
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, trigger.CommandType.Name, ExecutionMode.Sequential);
#endif
                try
                {
                    // P1-14 fix: re-inject on retry so the command state is refreshed,
                    // and run through the decorator pipeline like normal commands.
                    if (retryCount > 0)
                    {
                        _container.Inject(command);
                    }

                    if (command is ICompositeCommand syncCompCmd)
                    {
                        ExecuteWithDecorators(syncCompCmd, () => syncCompCmd.Execute(context));
                    }
                    else if (command is ICommand syncCmd)
                    {
                        ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                    }
                    else if (command is IAsyncCompositeCommand asyncCompCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        Interlocked.Increment(ref _inFlightAsyncCommands);
                        inFlightIncremented = true;
                        try
                        {
                            if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                            {
                                await ExecuteWithDecoratorsAsync(asyncCompCmd, async () => await asyncCompCmd.ExecuteAsync(context, ct));
                            }
                            else
                            {
                                await asyncCompCmd.ExecuteAsync(context, ct);
                            }
                        }
                        finally
                        {
                            if (inFlightIncremented)
                            {
                                Interlocked.Decrement(ref _inFlightAsyncCommands);
                                inFlightIncremented = false;
                            }
                        }
                    }
                    else if (command is IAsyncCommand asyncCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        Interlocked.Increment(ref _inFlightAsyncCommands);
                        inFlightIncremented = true;
                        try
                        {
                            if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                            {
                                await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
                            }
                            else
                            {
                                await asyncCmd.ExecuteAsync(ct);
                            }
                        }
                        finally
                        {
                            if (inFlightIncremented)
                            {
                                Interlocked.Decrement(ref _inFlightAsyncCommands);
                                inFlightIncremented = false;
                            }
                        }
                    }
                    shouldRun = false;
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = HandleCommandErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
                    if (inFlightIncremented)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
                    }
                    if (command != null && !shouldRun)
                    {
                        _poolManager.ReturnCommand(trigger.CommandType, command);
                    }
                }
            }
        }

        private void ExecuteCompositeCommandAsync(CompositeTriggerState trigger, object command, CompositeContext context)
        {
            SafeAsyncRunner.Run(() => ExecuteCompositeCommandAsyncCore(trigger, command, context), 
                $"Composite command '{trigger.CommandType.FullName}' failed.");
        }

        private void ExecuteCompositeCommand(CompositeTriggerState trigger, CompositeContext context)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
                object command = null;
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, trigger.CommandType.Name, ExecutionMode.Sequential);
#endif
                try
                {
                    command = _poolManager.GetCommand(trigger.CommandType);
                    _container.Inject(command);
                    bool hasDecorators = _context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0;

                    if (command is ICompositeCommand compCmd)
                    {
                        // Composite payload support: pass the captured signal context to the command.
                        if (hasDecorators)
                        {
                            ExecuteWithDecorators(compCmd, () => compCmd.Execute(context));
                        }
                        else
                        {
                            compCmd.Execute(context);
                        }
                    }
                    else if (command is ICommand syncCmd)
                    {
                        // P1-14 fix: composite commands run through the decorator pipeline.
                        if (hasDecorators)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
                        else
                        {
                            syncCmd.Execute();
                        }
                    }
                    else if (command is IAsyncCompositeCommand || command is IAsyncCommand)
                    {
                        var cmdForAsync = command;
                        command = null; // Prevent finally from returning it; async method owns it now
                        ExecuteCompositeCommandAsync(trigger, cmdForAsync, context);
                        shouldRun = false;
                        return;
                    }
                    shouldRun = false;
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = HandleCommandErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(trigger.CommandType, command);
                    }
                }
            }
        }

        private void BroadcastCrossContext<T>(T signal, string scopeTag) where T : struct
        {
            var contexts = _contextResolver.GetActiveContexts();
            for (int i = 0; i < contexts.Count; i++)
            {
                var targetCtx = contexts[i];
                if (targetCtx == _context) continue; // Skip self

                // Check scope tag
                if (!string.IsNullOrEmpty(scopeTag))
                {
                    if (targetCtx is Context concreteCtx && concreteCtx.ScopeTag == scopeTag)
                    {
                        concreteCtx.SignalBusInternal.FireCrossContext(signal);
                    }
                }
                else
                {
                    if (targetCtx is Context concreteCtx)
                    {
                        concreteCtx.SignalBusInternal.FireCrossContext(signal);
                    }
                }
            }
        }

        public void FireCrossContext<T>(T signal) where T : struct
        {
            FireInternal(signal, isCrossContextSource: true);
        }

        /// <summary>
        /// P0-4 fix: async-aware dispatch used by queued/replayed signal paths
        /// (<see cref="HybridQueue"/> drains, network replay). If the signal has async
        /// handlers or subscriptions, it is routed through the async path fire-and-forget
        /// (with error capture) instead of throwing <see cref="NexusSyncAsyncMismatchException"/>.
        /// </summary>
        internal void FireQueued<T>(T signal) where T : struct
        {
            bool hasAsync = (_hasAsyncHandlerReadCopy.TryGetValue(typeof(T), out var flag) && flag)
                || HasAsyncSubscriptions(typeof(T));
            if (hasAsync)
            {
                SafeAsyncRunner.Run(() => FireInternalAsync(signal, isCrossContextSource: false),
                    $"Queued async dispatch failed for signal '{typeof(T).FullName}'");
            }
            else
            {
                FireInternal(signal, isCrossContextSource: false);
            }
        }

        private RecoveryAction HandleCommandErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
        {
            if (ex is OperationCanceledException || ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is OperationCanceledException || ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                // P1-3 fix: preserve the original stack trace when rethrowing.
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                NexusRuntime.Logger?.LogException(ex);
                return RecoveryAction.Abort;
            }

            NexusRuntime.Logger?.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        FireFailedSignalSafe(failedSignal);
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null && IsSyncCapableFallbackType(decision.FallbackCommandType, signal))
                        {
                            ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                        }
                        else if (decision.FallbackCommandType != null)
                        {
                            // Reject fallback types that cannot execute in this (sync) context —
                            // async-only types or types implementing no supported command interface —
                            // so we neither silently no-op nor recurse forever on the same decision.
                            NexusRuntime.Logger?.LogError($"[Nexus] Fallback command '{decision.FallbackCommandType.Name}' cannot execute synchronously for signal '{signal?.GetType().Name ?? "unknown"}'. Treating as Skip.");
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            NexusRuntime.Logger?.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            FireFailedSignalSafe(failedSignal);
            return RecoveryAction.Skip;
        }

        private async ValueTask<RecoveryAction> HandleCommandErrorWithDecisionAsync(Exception ex, Type commandType, object signal, int retryCount, CancellationToken ct)
        {
            if (ex is OperationCanceledException || ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is OperationCanceledException || ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                // P1-3 fix: preserve the original stack trace when rethrowing.
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                NexusRuntime.Logger?.LogException(ex);
                return RecoveryAction.Abort;
            }

            NexusRuntime.Logger?.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        // P0-4 fix: async-safe dispatch — awaits the full handler chain
                        // and captures errors instead of throwing a sync/async mismatch.
                        await FireAsyncAndForget(failedSignal);
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null && IsValidFallbackType(decision.FallbackCommandType, signal))
                        {
                            // E-4/P0-1-aligned: recognize generic-only async fallback commands too.
                            var isAsync = typeof(IAsyncCommand).IsAssignableFrom(decision.FallbackCommandType)
                                || ImplementsGenericInterface(decision.FallbackCommandType, typeof(IAsyncCommand<>));
                            if (isAsync)
                            {
                                await ExecuteCommandAsync(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, true), signal, ct);
                            }
                            else
                            {
                                ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                            }
                        }
                        else if (decision.FallbackCommandType != null)
                        {
                            NexusRuntime.Logger?.LogError($"[Nexus] Fallback command '{decision.FallbackCommandType.Name}' implements no supported command interface for signal '{signal?.GetType().Name ?? "unknown"}'. Treating as Skip.");
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            NexusRuntime.Logger?.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            // P0-4 fix: async-safe dispatch of the failure signal.
            await FireAsyncAndForget(failedSignal);
            return RecoveryAction.Skip;
        }

        /// <summary>
        /// True if <paramref name="fallbackType"/> implements a command interface usable by the
        /// object-based async dispatch paths for <paramref name="signal"/>: non-generic
        /// ICommand/IAsyncCommand, or the generic ICommand&lt;TSignal&gt;/IAsyncCommand&lt;TSignal&gt;
        /// matching the signal type.
        /// </summary>
        private static bool IsValidFallbackType(Type fallbackType, object signal)
        {
            if (typeof(ICommand).IsAssignableFrom(fallbackType) || typeof(IAsyncCommand).IsAssignableFrom(fallbackType))
                return true;
            if (signal == null) return false;
            var signalType = signal.GetType();
            return typeof(ICommand<>).MakeGenericType(signalType).IsAssignableFrom(fallbackType)
                || typeof(IAsyncCommand<>).MakeGenericType(signalType).IsAssignableFrom(fallbackType);
        }

        /// <summary>
        /// True if <paramref name="fallbackType"/> can execute <b>synchronously</b> for
        /// <paramref name="signal"/>: non-generic <see cref="ICommand"/> or the generic
        /// <see cref="ICommand{TSignal}"/> matching the signal type. Async-only types are rejected
        /// here because the sync error path has no way to await them (attempting dispatch would
        /// throw and re-enter the recovery strategy).
        /// </summary>
        private static bool IsSyncCapableFallbackType(Type fallbackType, object signal)
        {
            if (typeof(ICommand).IsAssignableFrom(fallbackType)) return true;
            if (signal == null) return false;
            return typeof(ICommand<>).MakeGenericType(signal.GetType()).IsAssignableFrom(fallbackType);
        }

        /// <summary>
        /// Returns a cached dispatcher that invokes <see cref="ICommand{TSignal}"/>.Execute on a
        /// generic-only command (or null if the command type does not implement that interface).
        /// Used by the object-based fallback paths so generic-only commands are not silently skipped.
        /// </summary>
        private static Action<object, object> GetGenericSyncDispatcher(Type commandType, Type signalType)
        {
            var key = (commandType, signalType);
            if (s_genericSyncDispatchCache.TryGetValue(key, out var cached)) return cached;

            var genericInterface = typeof(ICommand<>).MakeGenericType(signalType);
            if (!genericInterface.IsAssignableFrom(commandType)) return null;
            var method = genericInterface.GetMethod("Execute");
            Action<object, object> dispatcher = (cmd, sig) => method.Invoke(cmd, new[] { sig });
            s_genericSyncDispatchCache.TryAdd(key, dispatcher);
            return dispatcher;
        }

        /// <summary>
        /// Returns a cached dispatcher that invokes <see cref="IAsyncCommand{TSignal}"/>.ExecuteAsync on
        /// a generic-only async command (or null if the command type does not implement that interface).
        /// </summary>
        private static Func<object, object, CancellationToken, ValueTask> GetGenericAsyncDispatcher(Type commandType, Type signalType)
        {
            var key = (commandType, signalType);
            if (s_genericAsyncDispatchCache.TryGetValue(key, out var cached)) return cached;

            var genericInterface = typeof(IAsyncCommand<>).MakeGenericType(signalType);
            if (!genericInterface.IsAssignableFrom(commandType)) return null;
            var method = genericInterface.GetMethod("ExecuteAsync");
            Func<object, object, CancellationToken, ValueTask> dispatcher = (cmd, sig, ct) =>
            {
                var result = method.Invoke(cmd, new[] { sig, ct });
                return (ValueTask)result;
            };
            s_genericAsyncDispatchCache.TryAdd(key, dispatcher);
            return dispatcher;
        }

        private void ExecuteWithDecorators(object command, Action next)
        {
            if (_context is Context ctx && ctx.PluginsReadOnlyCopy.Count > 0)
            {
                var snapshot = ctx.PluginsReadOnlyCopy;
                Action current = next;
                for (int i = snapshot.Count - 1; i >= 0; i--)
                {
                    var decorators = snapshot[i].context.Decorators;
                    for (int j = decorators.Count - 1; j >= 0; j--)
                    {
                        var d = decorators[j];
                        var prev = current;
                        current = () => d.DecorateExecute(command, prev);
                    }
                }
                current();
            }
            else
            {
                next();
            }
        }

        private async ValueTask ExecuteWithDecoratorsAsync(object command, Func<ValueTask> next)
        {
            if (_context is Context ctx && ctx.PluginsReadOnlyCopy.Count > 0)
            {
                var snapshot = ctx.PluginsReadOnlyCopy;
                Func<ValueTask> current = next;
                for (int i = snapshot.Count - 1; i >= 0; i--)
                {
                    var decorators = snapshot[i].context.Decorators;
                    for (int j = decorators.Count - 1; j >= 0; j--)
                    {
                        var d = decorators[j];
                        var prev = current;
                        current = async () => await d.DecorateExecuteAsync(command, prev);
                    }
                }
                await current();
            }
            else
            {
                await next();
            }
        }

        public void Dispose()
        {
            lock (_subLock)
            {
                // Snapshot the nodes before disposing: RawSubscription.Dispose() re-enters
                // Unsubscribe → SweepDeadNodes, which mutates _subscriptions. Enumerating the
                // live dictionary while disposing would throw InvalidOperationException
                // ("Collection was modified") during teardown. Clear the dictionaries first,
                // then dispose outside the enumeration.
                List<SubscriptionNode> nodes = null;
                foreach (var kvp in _subscriptions)
                {
                    var current = kvp.Value;
                    while (current != null)
                    {
                        (nodes ??= new List<SubscriptionNode>()).Add(current);
                        current = current.Next;
                    }
                }
                _subscriptions.Clear();
                _subscriptionsReadCopy = new Dictionary<Type, SubscriptionNode>();

                if (nodes != null)
                {
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        var node = nodes[i];
                        if (node.IsActive && node.RawSubscription is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        SubscriptionNodePool.Return(node);
                    }
                }
            }

            if (Volatile.Read(ref _inFlightAsyncCommands) > 0)
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] SignalBus disposed while {_inFlightAsyncCommands} async command(s) are still in-flight. This may cause unexpected behavior.");
            }

            lock (_handlerReadLock)
            {
                _commandHandlers.Clear();
                _hasAsyncHandler.Clear();
                _handlersSnapshotDirty = true;
                _commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>();
                _hasAsyncHandlerReadCopy = new Dictionary<Type, bool>();
            }

            lock (_compositeLock)
            {
                _compositeTriggersBySignal.Clear();
                _allCompositeTriggers.Clear();
            }
        }

        internal static void ClearStaticCaches()
        {
            s_signalSetterCache.Clear();
            s_genericSyncDispatchCache.Clear();
            s_genericAsyncDispatchCache.Clear();
            s_crossContextCache.Clear();
            lock (s_listPool)
            {
                s_listPool.Clear();
            }
            SubscriptionNodePool.Clear();
            OnUnhandledException = null;
        }
    }

}
