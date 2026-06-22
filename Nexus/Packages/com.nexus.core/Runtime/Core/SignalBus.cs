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


    [Preserve]
    public class SignalBus : ISignalBus, IDisposable
    {
        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;

        private readonly Dictionary<Type, List<CommandHandlerInfo>> _commandHandlers = new();
        private readonly Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersBySignal = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();

        public IReadOnlyDictionary<Type, List<CommandHandlerInfo>> CommandHandlers => _commandHandlers;

        private readonly Dictionary<Type, List<object>> _subscriptions = new();

        // Precomputed cache: does this signal type have at least one async handler?
        // Used by FireInternal to decide whether to delegate to the async path.
        private readonly Dictionary<Type, bool> _hasAsyncHandler = new();
        
        [ThreadStatic]
        private static int s_stackDepth;
        private const int MaxStackDepth = 50;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), MemberInfo> s_signalFieldCache = new();

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

        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync)
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
            if (signalTypes.Length > 64)
                throw new ArgumentException($"Composite command supports a maximum of 64 signal types. Received {signalTypes.Length}.", nameof(signalTypes));

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

        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var list))
            {
                list = new List<object>();
                _subscriptions[type] = list;
            }

            SignalSubscription<T> sub = null;
            sub = new SignalSubscription<T>(handler, _context.LifetimeToken, () =>
            {
                lock (list)
                {
                    list.Remove(sub);
                }
            });

            lock (list)
            {
                list.Add(sub);
            }
            return sub;
        }

        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var list))
            {
                list = new List<object>();
                _subscriptions[type] = list;
            }

            AsyncSignalSubscription<T> sub = null;
            sub = new AsyncSignalSubscription<T>(handler, _context.LifetimeToken, () =>
            {
                lock (list)
                {
                    list.Remove(sub);
                }
            });

            lock (list)
            {
                list.Add(sub);
            }
            return sub;
        }

        private void FireInternal<T>(T signal, bool isCrossContextSource) where T : struct
        {
            var type = typeof(T);

            // Plan §1.4.1 — If this signal has ANY async handlers registered,
            // delegate to the async path to preserve Sequential ordering guarantees.
            // The async path properly awaits each handler in priority order.
            // Sync-only signals take the fast path below with zero async overhead.
            bool hasAsync = _hasAsyncHandler.TryGetValue(type, out var asyncFlag) && asyncFlag;
            bool hasAsyncSubscriptions = HasAsyncSubscriptions(type);

            if (hasAsync || hasAsyncSubscriptions)
            {
                // Delegate to async path — this is the ONLY correct way to handle
                // async handlers from a sync call site. We use async void pattern
                // (Unity-compatible) to bridge the sync→async boundary.
                FireInternalAsyncFromSync(signal, isCrossContextSource);
                return;
            }

            // === FAST PATH: All handlers are synchronous ===
            s_stackDepth++;
            if (s_stackDepth > MaxStackDepth)
            {
                s_stackDepth = 0;
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
                // Run plugins' SignalInterceptors (snapshot to avoid modification during iteration)
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.Plugins.Count > 0)
                {
                    object boxedSignal = signal;
                    var pluginSnapshot = ctx.GetPluginsSnapshot();
                    foreach (var p in pluginSnapshot)
                    {
                        foreach (var interceptor in p.context.Interceptors)
                        {
                            if (!interceptor.Intercept(ref boxedSignal))
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
                if (_subscriptions.TryGetValue(type, out var subs))
                {
                    List<object> subsCopy;
                    lock (subs)
                    {
                        subsCopy = new List<object>(subs);
                    }
                    for (int i = 0; i < subsCopy.Count; i++)
                    {
                        if (subsCopy[i] is SignalSubscription<T> syncSub)
                        {
                            syncSub.Invoke(signal);
                        }
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
                s_stackDepth--;
            }
        }

        /// <summary>
        /// Bridge method: sync Fire() with async handlers. Uses async void (Unity-compatible)
        /// to properly await all handlers in order. Exceptions are caught and logged
        /// via the standard recovery pipeline.
        /// </summary>
        private async void FireInternalAsyncFromSync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            try
            {
                await FireInternalAsync(signal, isCrossContextSource);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                UnityEngine.Debug.LogError($"[Nexus] Async bridge failed for signal '{typeof(T).FullName}': {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Checks if a signal type has any async subscriptions (SubscribeAsync).
        /// </summary>
        private bool HasAsyncSubscriptions(Type signalType)
        {
            if (!_subscriptions.TryGetValue(signalType, out var subs))
                return false;

            lock (subs)
            {
                for (int i = 0; i < subs.Count; i++)
                {
                    // AsyncSignalSubscription<T> type name starts with "AsyncSignal"
                    var subType = subs[i].GetType();
                    if (subType.Name.StartsWith("AsyncSignalSubscription"))
                        return true;
                }
            }
            return false;
        }

        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            s_stackDepth++;
            if (s_stackDepth > MaxStackDepth)
            {
                s_stackDepth = 0;
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

                // Run plugins' SignalInterceptors (snapshot to avoid modification during iteration)
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.Plugins.Count > 0)
                {
                    object boxedSignal = signal;
                    var pluginSnapshot = ctx.GetPluginsSnapshot();
                    foreach (var p in pluginSnapshot)
                    {
                        foreach (var interceptor in p.context.Interceptors)
                        {
                            if (!interceptor.Intercept(ref boxedSignal))
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
                if (_subscriptions.TryGetValue(type, out var subs))
                {
                    List<object> subsCopy;
                    lock (subs)
                    {
                        subsCopy = new List<object>(subs);
                    }
                    
                    foreach (var sub in subsCopy)
                    {
                        if (sub is SignalSubscription<T> syncSub)
                        {
                            syncSub.Invoke(signal);
                        }
                        else if (sub is AsyncSignalSubscription<T> asyncSub)
                        {
                            await asyncSub.InvokeAsync(signal, _context.LifetimeToken);
                        }
                    }
                }

                // Process commands
                if (_commandHandlers.TryGetValue(type, out var handlers))
                {
                    if (handlers.Count > 0 && handlers[0].Mode == ExecutionMode.Concurrent)
                    {
                        // Run concurrently
                        var tasks = new ValueTask[handlers.Count];
                        for (int i = 0; i < handlers.Count; i++)
                        {
                            tasks[i] = ExecuteCommandAsync(handlers[i], signal, _context.LifetimeToken);
                        }
                        
                        foreach (var task in tasks)
                        {
                            await task;
                        }
                    }
                    else
                    {
                        // Run sequentially
                        foreach (var handler in handlers)
                        {
                            if (handler.IsAsync)
                            {
                                await ExecuteCommandAsync(handler, signal, _context.LifetimeToken);
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
                s_stackDepth--;
            }
        }

        private static class SignalInjector<TSignal> where TSignal : struct
        {
            private static readonly Dictionary<Type, Action<object, TSignal>> s_setters = new();
            private static readonly Dictionary<Type, MemberInfo> s_memberCache = new();
            private static readonly Dictionary<Type, bool> s_hasMember = new();

            public static void Inject(object command, TSignal signal)
            {
                var commandType = command.GetType();
                
                if (!s_hasMember.TryGetValue(commandType, out var hasMember))
                {
                    MemberInfo foundMember = null;
                    var fields = commandType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        if (field.FieldType == typeof(TSignal) || (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) && field.FieldType.IsAssignableFrom(typeof(TSignal))))
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
                            if (prop.PropertyType == typeof(TSignal) && prop.CanWrite)
                            {
                                foundMember = prop;
                                break;
                            }
                        }
                    }

                    hasMember = foundMember != null;
                    s_hasMember[commandType] = hasMember;
                    if (hasMember)
                    {
                        s_memberCache[commandType] = foundMember;
                        var setter = CreateSetter(commandType, foundMember);
                        if (setter != null)
                        {
                            s_setters[commandType] = setter;
                        }
                    }
                }

                if (hasMember)
                {
                    if (s_setters.TryGetValue(commandType, out var compiledSetter))
                    {
                        compiledSetter(command, signal);
                    }
                    else
                    {
                        var member = s_memberCache[commandType];
                        if (member is FieldInfo f)
                            f.SetValue(command, signal);
                        else if (member is PropertyInfo p)
                            p.SetValue(command, signal);
                    }
                }
            }

            private static Action<object, TSignal> CreateSetter(Type commandType, MemberInfo member)
            {
                try
                {
                    var targetExp = System.Linq.Expressions.Expression.Parameter(typeof(object), "target");
                    var valueExp = System.Linq.Expressions.Expression.Parameter(typeof(TSignal), "value");
                    var castTarget = System.Linq.Expressions.Expression.Convert(targetExp, commandType);
                    
                    System.Linq.Expressions.Expression memberExp = null;
                    if (member is FieldInfo f)
                        memberExp = System.Linq.Expressions.Expression.Field(castTarget, f);
                    else if (member is PropertyInfo p)
                        memberExp = System.Linq.Expressions.Expression.Property(castTarget, p);

                    if (memberExp != null)
                    {
                        var assignExp = System.Linq.Expressions.Expression.Assign(memberExp, valueExp);
                        var lambda = System.Linq.Expressions.Expression.Lambda<Action<object, TSignal>>(assignExp, targetExp, valueExp);
                        return lambda.Compile();
                    }
                }
                catch
                {
                    // Fallback to reflection
                }

                if (member is FieldInfo fieldInfo)
                {
                    return (target, val) => fieldInfo.SetValue(target, val);
                }
                else if (member is PropertyInfo propInfo)
                {
                    return (target, val) => propInfo.SetValue(target, val);
                }
                return null;
            }
        }

        private void ExecuteCommand<TSignal>(CommandHandlerInfo handler, TSignal signal) where TSignal : struct
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
                    
                    if (command is ICommand<TSignal> genericSyncCmd)
                    {
                        ExecuteWithDecorators(genericSyncCmd, () => genericSyncCmd.Execute(signal));
                    }
                    else if (command is IAsyncCommand<TSignal> genericAsyncCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        ExecuteWithDecoratorsAsync(genericAsyncCmd, async () => await genericAsyncCmd.ExecuteAsync(signal, ct)).AsTask().GetAwaiter().GetResult();
                    }
                    else
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        UnityEngine.Debug.LogWarning($"[Nexus Performance Warning] Command '{handler.CommandType.Name}' handles signal '{typeof(TSignal).Name}' but does not implement ICommand<{typeof(TSignal).Name}> or IAsyncCommand<{typeof(TSignal).Name}>. Fallback to reflection injection is used, causing performance overhead/boxing on AOT.");
#endif
                        SignalInjector<TSignal>.Inject(command, signal);
 
                        if (command is ICommand syncCmd)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
                        else if (command is IAsyncCommand asyncCmd)
                        {
                            var ct = _context?.LifetimeToken ?? CancellationToken.None;
                            ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct)).AsTask().GetAwaiter().GetResult();
                        }
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
                    else if (command is IAsyncCommand asyncCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct)).AsTask().GetAwaiter().GetResult();
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        UnityEngine.Debug.LogWarning($"[Nexus Performance Warning] Command '{handler.CommandType.Name}' handles signal '{typeof(TSignal).Name}' but does not implement ICommand<{typeof(TSignal).Name}> or IAsyncCommand<{typeof(TSignal).Name}>. Fallback to reflection injection is used, causing performance overhead/boxing on AOT.");
#endif
                        SignalInjector<TSignal>.Inject(command, signal);
 
                        if (command is IAsyncCommand asyncCmd)
                        {
                            await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
                        }
                        else if (command is ICommand syncCmd)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
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

            if (s_signalFieldCache.TryGetValue(cacheKey, out var member))
            {
                if (member is FieldInfo f)
                    f.SetValue(command, signal);
                else if (member is PropertyInfo p)
                    p.SetValue(command, signal);
                return;
            }

            var fields = commandType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType == signalType || (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) && field.FieldType.IsInstanceOfType(signal)))
                {
                    field.SetValue(command, signal);
                    s_signalFieldCache[cacheKey] = field;
                    return;
                }
            }

            var properties = commandType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.PropertyType == signalType && prop.CanWrite)
                {
                    prop.SetValue(command, signal);
                    s_signalFieldCache[cacheKey] = prop;
                    return;
                }
            }
        }

        private void ProcessCompositeTriggers(Type signalType)
        {
            if (!_compositeTriggersBySignal.TryGetValue(signalType, out var triggers))
                return;

            foreach (var trigger in triggers)
            {
                if (trigger.IsCompleted) continue;

                // Find index of signalType in required signals
                int index = Array.IndexOf(trigger.RequiredSignals, signalType);
                if (index >= 0)
                {
                    trigger.CurrentMask |= (1UL << index);
                    
                    if (trigger.CurrentMask == trigger.TargetMask)
                    {
                        // Trigger the command!
                        ExecuteCompositeCommand(trigger);

                        if (trigger.OneShot)
                        {
                            trigger.IsCompleted = true;
                        }
                        else
                        {
                            // Reset bitmask
                            trigger.CurrentMask = 0;
                        }
                    }
                }
            }
        }

        private async void ExecuteCompositeCommandAsync(CompositeTriggerState trigger, object command)
        {
#if NEXUS_DEBUG
            int traceId = NexusTrace.BeginEvent(TraceEventType.Command, trigger.CommandType.Name, ExecutionMode.Sequential);
#endif
            bool inFlightIncremented = false;
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
#if NEXUS_DEBUG
                NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
            }
            catch (Exception ex)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                int retry = 0;
                HandleCommandErrorWithDecision(ex, trigger.CommandType, null, ref retry);
            }
            finally
            {
                if (inFlightIncremented)
                {
                    Interlocked.Decrement(ref _inFlightAsyncCommands);
                }
                if (command != null)
                {
                    _poolManager.ReturnCommand(trigger.CommandType, command);
                }
            }
        }

        private void ExecuteCompositeCommand(CompositeTriggerState trigger)
        {
#if NEXUS_DEBUG
            int traceId = NexusTrace.BeginEvent(TraceEventType.Command, trigger.CommandType.Name, ExecutionMode.Sequential);
#endif
            object command = null;
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
                    // Async composite — dispatch via async void with proper tracking
                    // Pass the command to the async method; it will handle pool return
                    var cmdForAsync = command;
                    command = null; // Prevent finally from returning it; async method owns it now
                    ExecuteCompositeCommandAsync(trigger, cmdForAsync);
                    return;
                }
#if NEXUS_DEBUG
                NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
            }
            catch (Exception ex)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                int retry = 0;
                HandleCommandErrorWithDecision(ex, trigger.CommandType, null, ref retry);
            }
            finally
            {
                if (command != null)
                {
                    _poolManager.ReturnCommand(trigger.CommandType, command);
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
            if (ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
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
            if (ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
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
            // Dispose all active subscriptions using a copy to avoid modification exceptions
            foreach (var kvp in _subscriptions)
            {
                var subsCopy = new List<object>(kvp.Value);
                foreach (var sub in subsCopy)
                {
                    if (sub is IDisposable disposable)
                        disposable.Dispose();
                }
            }
            _subscriptions.Clear();

            // Warn if there are in-flight async commands that haven't completed
            if (_inFlightAsyncCommands > 0)
            {
                UnityEngine.Debug.LogWarning($"[Nexus] SignalBus disposed while {_inFlightAsyncCommands} async command(s) are still in-flight. This may cause unexpected behavior.");
            }

            _commandHandlers.Clear();
            _compositeTriggersBySignal.Clear();
            _allCompositeTriggers.Clear();
        }
    }

}
