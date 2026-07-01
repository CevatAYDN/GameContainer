using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Unity.Profiling;

namespace Nexus.Core
{


    internal class SubscriptionNode
    {
        public object Handler;
        public object RawSubscription;
        public bool IsActive = true;
        public bool IsAsync;
        public SubscriptionNode Next;

        public void Reset()
        {
            Handler = null;
            RawSubscription = null;
            IsActive = true;
            IsAsync = false;
            Next = null;
        }
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
                    node.Handler = handler;
                    node.RawSubscription = rawSub;
                    node.IsActive = true;
                    node.IsAsync = isAsync;
                    node.Next = null;
                    return node;
                }
            }
            return new SubscriptionNode { Handler = handler, RawSubscription = rawSub, IsAsync = isAsync };
        }

        public static void Return(SubscriptionNode node)
        {
            node.Reset();
            lock (s_pool)
            {
                s_pool.Push(node);
            }
        }

        public static void Clear()
        {
            lock (s_pool)
            {
                s_pool.Clear();
            }
        }
    }

    /// <summary>
    /// Runs async work in a fire-and-forget manner. Uses async Task internally
    /// (not async void) so unhandled exceptions are caught by the Task infrastructure
    /// rather than crashing the process on the Unity SynchronizationContext.
    /// </summary>
    internal static class SafeAsyncRunner
    {
        public static void Run(Func<ValueTask> func, string errorContext)
        {
            // Fire-and-forget: the Task is not awaited, but the async state machine
            // uses async Task (not async void), meaning exceptions are captured on
            // the Task and never escape to the Unity SynchronizationContext.
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
                UnityEngine.Debug.LogError($"[Nexus] {errorContext}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    [Preserve]
    public class SignalBus : ISignalBus, IDisposable
    {
        public static event Action<Exception, string> OnUnhandledException;

        internal static void RaiseUnhandledException(Exception ex, string context)
        {
            OnUnhandledException?.Invoke(ex, context);
        }

        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;

        private readonly Dictionary<Type, List<CommandHandlerInfo>> _commandHandlers = new();
        private readonly Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersBySignal = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();

        public IReadOnlyDictionary<Type, List<CommandHandlerInfo>> CommandHandlers => _commandHandlers;

        /// <summary>
        /// Returns all registered signal→handler mappings.
        /// Populated by both fluent API (BindSignal/To) and attribute-based discovery.
        /// </summary>
        public IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers
        {
            get
            {
                // Allocate-once wrapper: SignalBus is fully configured before runtime use
                if (_cachedRegistered == null)
                {
                    var dict = new Dictionary<Type, IReadOnlyList<CommandHandlerInfo>>(_commandHandlers.Count);
                    foreach (var kvp in _commandHandlers)
                        dict[kvp.Key] = kvp.Value;
                    _cachedRegistered = dict;
                }
                return _cachedRegistered;
            }
        }
        private Dictionary<Type, IReadOnlyList<CommandHandlerInfo>> _cachedRegistered;

        private readonly Dictionary<Type, SubscriptionNode> _subscriptions = new();
        private readonly object _subLock = new();
        private readonly object _compositeLock = new();
        private bool _pendingCleanups;

        // Precomputed cache: does this signal type have at least one async handler?
        // Used by FireInternal to decide whether to delegate to the async path.
        private readonly Dictionary<Type, bool> _hasAsyncHandler = new();
        
        private static readonly System.Threading.AsyncLocal<int> s_stackDepth = new();
        private const int MaxStackDepth = 50;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_signalSetterCache = new();

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
        {
            _container = container;
            _poolManager = poolManager;
            _context = context;
        }

        private static bool ImplementsGenericInterface(Type type, Type genericInterface)
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

            list.Add(new CommandHandlerInfo(commandType, mode, priority, isAsync));
            
            // Bind command type in DI so CommandPoolManager can resolve it
            _container.Bind(commandType, isSingleton: false);

            // Update async handler cache — if any handler is async, mark the signal type
            if (isAsync)
            {
                _hasAsyncHandler[signalType] = true;
            }
            else if (!_hasAsyncHandler.ContainsKey(signalType))
            {
                _hasAsyncHandler[signalType] = false;
            }

            // Sort by priority descending
            if (mode != ExecutionMode.Concurrent)
            {
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        public void RegisterCompositeCommand(Type[] signalTypes, Type commandType, bool oneShot, int priority, bool isAsync)
        {
            if (signalTypes == null || signalTypes.Length == 0)
                throw new ArgumentException("Composite command requires at least one signal type.", nameof(signalTypes));
            if (signalTypes.Length > 64 || signalTypes.Length == 0)
                throw new ArgumentException($"Composite command requires between 1 and 64 signal types. Received {signalTypes.Length}.", nameof(signalTypes));

            var state = new CompositeTriggerState(commandType, signalTypes, oneShot, priority);
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
                UnityEngine.Debug.LogError($"[Nexus] Async signal '{typeof(T).Name}' timed out after {timeoutMilliseconds}ms.");
                throw;
            }
        }

        public async ValueTask FireAsyncAndForget<T>(T signal, Action<Exception> onError = null) where T : struct
        {
            try
            {
                await FireInternalAsync(signal, isCrossContextSource: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (onError != null)
                {
                    onError(ex);
                }
                else
                {
                    OnUnhandledException?.Invoke(ex, $"FireAsyncAndForget failed for signal '{typeof(T).FullName}'");
                    UnityEngine.Debug.LogError($"[Nexus] FireAsyncAndForget signal '{typeof(T).Name}' failed: {ex.Message}\n{ex.StackTrace}");
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
        }

        private void SweepDeadNodes()
        {
            lock (_subLock)
            {
                if (!_pendingCleanups) return;
                _pendingCleanups = false;

                var keys = new List<Type>(_subscriptions.Keys);
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
            }
        }

        private void FireInternal<T>(T signal, bool isCrossContextSource) where T : struct
        {
            var type = typeof(T);

            NexusRuntime.Metrics.RecordSignalDispatched();
            NexusRuntime.Metrics.RecordTrace($"▶ {typeof(T).Name}");

            // Plan §1.4.1 — If this signal has ANY async handlers registered,
            // delegate to the async path to preserve Sequential ordering guarantees.
            // The async path properly awaits each handler in priority order.
            // Sync-only signals take the fast path below with zero async overhead.
            bool hasAsync = _hasAsyncHandler.TryGetValue(type, out var asyncFlag) && asyncFlag;
            bool hasAsyncSubscriptions = HasAsyncSubscriptions(type);

            if (hasAsync || hasAsyncSubscriptions)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                throw new InvalidOperationException(
                    $"Synchronous Fire() was called for signal '{typeof(T).FullName}', but it has asynchronous handlers or subscriptions registered. " +
                    "To preserve sequential ordering and prevent race conditions, you must invoke this signal using FireAsync() and await its completion, or use FireAsyncAndForget().");
#else
                UnityEngine.Debug.LogError(
                    $"[Nexus] Synchronous Fire() was called for signal '{typeof(T).FullName}', but it has asynchronous handlers or subscriptions registered. " +
                    "This violates sequential ordering guarantees and will run fire-and-forget. Please use FireAsync() or FireAsyncAndForget().");
                _ = FireInternalAsyncFromSync(signal, isCrossContextSource);
                return;
#endif
            }

            // === FAST PATH: All handlers are synchronous ===
            s_stackDepth.Value++;
            if (s_stackDepth.Value > MaxStackDepth)
            {
                s_stackDepth.Value = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
#else
                UnityEngine.Debug.LogError($"[Nexus] Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
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
                    var crossContextAttr = type.GetCustomAttribute<CrossContextAttribute>();
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // Process subscriptions (sync-only path — no async subs here)
                if (_subscriptions.TryGetValue(type, out var node))
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

                // Process commands (all sync in this path)
                if (_commandHandlers.TryGetValue(type, out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        ExecuteCommand(handler, signal);
                    }
                }

                // Process composite triggers
                ProcessCompositeTriggers(type);
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
#endif
            }
            catch (Exception)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
#endif
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
        /// Bridge method: sync Fire() with async handlers. Uses async void (Unity-compatible)
        /// to properly await all handlers in order. Exceptions are caught and logged
        /// via the standard recovery pipeline.
        /// </summary>
        private async ValueTask FireInternalAsyncFromSync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            try
            {
                await FireInternalAsync(signal, isCrossContextSource);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnUnhandledException?.Invoke(ex, $"Async bridge failed for signal '{typeof(T).FullName}'");
                UnityEngine.Debug.LogError($"[Nexus] Async bridge failed for signal '{typeof(T).FullName}': {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Checks if a signal type has any async subscriptions (SubscribeAsync).
        /// </summary>
        private bool HasAsyncSubscriptions(Type signalType)
        {
            if (!_subscriptions.TryGetValue(signalType, out var node))
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
                s_stackDepth.Value = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
#else
                UnityEngine.Debug.LogError($"[Nexus] Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
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
                    var crossContextAttr = type.GetCustomAttribute<CrossContextAttribute>();
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // Subscriptions
                if (_subscriptions.TryGetValue(type, out var node))
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
                                await asyncSub(signal, _context.LifetimeToken);
                            }
                        }
                        current = current.Next;
                    }
                }

                // Process commands
                if (_commandHandlers.TryGetValue(type, out var handlers))
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

                // Process composite triggers
                ProcessCompositeTriggers(type);
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
#endif
            }
            catch (Exception)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
#endif
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
            NexusRuntime.Metrics.RecordTrace($"  └ {handler.CommandType.Name}");

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
                        ExecuteWithDecorators(genericSyncCmd, () => genericSyncCmd.Execute(signal));
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
                        ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
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
                        UnityEngine.Debug.LogError($"[Nexus] Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
                        shouldRun = false;
                        break;
#endif
                    }
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
 
                    if (command is IAsyncCommand<TSignal> genericAsyncCmd)
                    {
                        await ExecuteWithDecoratorsAsync(genericAsyncCmd, async () => await genericAsyncCmd.ExecuteAsync(signal, ct));
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
                        UnityEngine.Debug.LogError($"[Nexus] Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
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
                        await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
                    }
                    else if (command is ICommand syncCmd)
                    {
                        ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
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

        private void InjectSignal(object command, object signal)
        {
            if (signal == null) return;

            var commandType = command.GetType();
            var signalType = signal.GetType();
            var cacheKey = (commandType, signalType);

            if (s_signalSetterCache.TryGetValue(cacheKey, out var setter))
            {
                setter(command, signal);
                return;
            }

            Action<object, object> newSetter = null;
            MemberInfo foundMember = null;

            var fields = commandType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType == signalType || (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) && field.FieldType.IsInstanceOfType(signal)))
                {
                    foundMember = field;
                    break;
                }
            }

            if (foundMember == null)
            {
                var properties = commandType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    if (prop.PropertyType == signalType && prop.CanWrite)
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
                UnityEngine.Debug.LogWarning($"[Nexus] Signal injection: no matching field or property found in '{commandType.Name}' for signal type '{signalType.Name}'. Command will execute with default signal values.");
                newSetter = (target, val) => { };
            }

            s_signalSetterCache[cacheKey] = newSetter;
            newSetter(command, signal);
        }

        private void ProcessCompositeTriggers(Type signalType)
        {
            if (!_compositeTriggersBySignal.TryGetValue(signalType, out var triggers))
                return;

            lock (_compositeLock)
            {
                foreach (var trigger in triggers)
                {
                    if (trigger.IsCompleted) continue;

                    int index = Array.IndexOf(trigger.RequiredSignals, signalType);
                    if (index >= 0)
                    {
                        trigger.CurrentMask |= (1UL << index);
                        
                        if (trigger.CurrentMask == trigger.TargetMask)
                        {
                            ExecuteCompositeCommand(trigger);

                            if (trigger.OneShot)
                            {
                                trigger.IsCompleted = true;
                            }
                            else
                            {
                                trigger.CurrentMask = 0;
                            }
                        }
                    }
                }
            }
        }

        private async ValueTask ExecuteCompositeCommandAsyncCore(CompositeTriggerState trigger, object command)
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
                    if (command is ICommand syncCmd)
                    {
                        syncCmd.Execute();
                    }
                    else if (command is IAsyncCommand asyncCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        Interlocked.Increment(ref _inFlightAsyncCommands);
                        inFlightIncremented = true;
                        try
                        {
                            await asyncCmd.ExecuteAsync(ct);
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

        private void ExecuteCompositeCommandAsync(CompositeTriggerState trigger, object command)
        {
            SafeAsyncRunner.Run(() => ExecuteCompositeCommandAsyncCore(trigger, command), 
                $"Composite command '{trigger.CommandType.FullName}' failed.");
        }

        private void ExecuteCompositeCommand(CompositeTriggerState trigger)
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

                    if (command is ICommand syncCmd)
                    {
                        syncCmd.Execute();
                    }
                    else if (command is IAsyncCommand asyncCmd)
                    {
                        var cmdForAsync = command;
                        command = null; // Prevent finally from returning it; async method owns it now
                        ExecuteCompositeCommandAsync(trigger, cmdForAsync);
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
            // Find other contexts through NexusRuntime
            var contexts = NexusRuntime.ActiveContexts;
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

        private RecoveryAction HandleCommandErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
        {
            if (ex is OperationCanceledException || ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is OperationCanceledException || ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                throw ex;
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                UnityEngine.Debug.LogException(ex);
                return RecoveryAction.Abort;
            }

            UnityEngine.Debug.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        Fire(failedSignal);
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null)
                        {
                            ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            UnityEngine.Debug.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    UnityEngine.Debug.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            Fire(failedSignal);
            return RecoveryAction.Skip;
        }

        private async ValueTask<RecoveryAction> HandleCommandErrorWithDecisionAsync(Exception ex, Type commandType, object signal, int retryCount, CancellationToken ct)
        {
            if (ex is OperationCanceledException || ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is OperationCanceledException || ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                throw ex;
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                UnityEngine.Debug.LogException(ex);
                return RecoveryAction.Abort;
            }

            UnityEngine.Debug.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        Fire(failedSignal);
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null)
                        {
                            var isAsync = typeof(IAsyncCommand).IsAssignableFrom(decision.FallbackCommandType);
                            if (isAsync)
                            {
                                await ExecuteCommandAsync(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, true), signal, ct);
                            }
                            else
                            {
                                ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                            }
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            UnityEngine.Debug.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    UnityEngine.Debug.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            Fire(failedSignal);
            return RecoveryAction.Skip;
        }

        private void ExecuteWithDecorators(object command, Action next)
        {
            if (_context is Context ctx && ctx.Plugins.Count > 0)
            {
                Action current = next;
                for (int i = ctx.Plugins.Count - 1; i >= 0; i--)
                {
                    var decorators = ctx.Plugins[i].context.Decorators;
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
            if (_context is Context ctx && ctx.Plugins.Count > 0)
            {
                Func<ValueTask> current = next;
                for (int i = ctx.Plugins.Count - 1; i >= 0; i--)
                {
                    var decorators = ctx.Plugins[i].context.Decorators;
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
                foreach (var kvp in _subscriptions)
                {
                    var current = kvp.Value;
                    while (current != null)
                    {
                        if (current.IsActive && current.RawSubscription is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                        var temp = current;
                        current = current.Next;
                        SubscriptionNodePool.Return(temp);
                    }
                }
                _subscriptions.Clear();
            }

            if (_inFlightAsyncCommands > 0)
            {
                UnityEngine.Debug.LogWarning($"[Nexus] SignalBus disposed while {_inFlightAsyncCommands} async command(s) are still in-flight. This may cause unexpected behavior.");
            }

            _commandHandlers.Clear();
            _compositeTriggersBySignal.Clear();
            _allCompositeTriggers.Clear();
        }

        internal static void ClearStaticCaches()
        {
            s_signalSetterCache.Clear();
            lock (s_listPool)
            {
                s_listPool.Clear();
            }
            SubscriptionNodePool.Clear();
            OnUnhandledException = null;
        }
    }

}
