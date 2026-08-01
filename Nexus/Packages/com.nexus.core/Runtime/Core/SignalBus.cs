using System;
using System.Collections.Generic;
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
        // ─── Registry wiring (single source of truth) ───
        // Command registration/handler metadata lives in CommandRegistry; subscription
        // storage/pool/sweep lives in SubscriptionRegistry. SignalBus owns dispatch,
        // recovery, and queueing only — it delegates ALL registration and subscription
        // state to the registries so there is exactly one storage layer (the harness's
        // differential suite proves the wired bus behaves identically to the standalone
        // registries).
        private readonly CommandRegistry _commandRegistry;
        private readonly SubscriptionRegistry _subscriptionRegistry;

        public static event Action<Exception, string> OnUnhandledException;

        internal static void RaiseUnhandledException(Exception ex, string context)
        {
            OnUnhandledException?.Invoke(ex, context);
        }

        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;
        private readonly IContextResolver _contextResolver;

        /// <summary>Registered signal→handler snapshots, owned by the command registry.</summary>
        public IReadOnlyDictionary<Type, List<CommandHandlerInfo>> CommandHandlers => _commandRegistry.CommandHandlers;

        /// <summary>
        /// Returns all registered signal→handler mappings.
        /// Populated by both fluent API (BindSignal/To) and attribute-based discovery.
        /// </summary>
        public IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers => _commandRegistry.RegisteredHandlers;

        /// <summary>
        /// P0-3 fix: cached per-signal-type trace label so the trace ring buffer
        /// stays allocation-free on the hot path.
        /// </summary>
        private static class SignalTraceLabel<T> where T : struct
        {
            public static readonly string Fire = "▶ " + typeof(T).Name;
        }

        // Serializes composite-trigger state mutation across concurrent dispatches of this
        // bus (the trigger tables themselves live in the command registry).
        private readonly object _compositeLock = new();

        // Reentrancy guard for the synchronous fast path. Thread-static by design: sync
        // dispatch is main-thread-only, so each thread tracks its own nesting and threads
        // never observe each other's depth.
        [ThreadStatic]
        private static int s_stackDepth;

        // Reentrancy guard for the async path. Must be async-local, NOT thread-static: an
        // async dispatch is incremented on the caller's thread but its continuations (and
        // the finally decrement) run on arbitrary thread-pool threads after an await. A
        // thread-static counter would leak +1 per suspended dispatch on the caller's slot
        // and push continuation slots negative, permanently drifting until MaxStackDepth
        // aborts every dispatch on every bus. AsyncLocal flows with the logical chain, so
        // increments and decrements always land on the same slot, recursion is detected
        // across threads, and concurrent queued/rollback dispatches never corrupt each
        // other's depth.
        private static readonly System.Threading.AsyncLocal<int> s_asyncStackDepth = new();

        private const int MaxStackDepth = 10;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

        // Shared reflection caches (signal setters, generic dispatchers, cross-context
        // attributes) live in CommandRegistry so every bus and the standalone registry share
        // ONE cache; cleared via CommandRegistry.ClearStaticCaches().

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
            _commandRegistry = new CommandRegistry(container);
            _subscriptionRegistry = new SubscriptionRegistry();
            // Restore the pre-refactor SignalBus semantics: an Unsubscribe while the bus is NOT
            // dispatching reclaims the node immediately; during dispatch it defers to unwind.
            _subscriptionRegistry.ImmediateSweepWhenIdle = true;
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
            // Registration, validation, snapshot rebuild, async-handler tracking, and DI binding
            // all live in the CommandRegistry — SignalBus only dispatches against the registry.
            _commandRegistry.RegisterCommand(signalType, commandType, mode, priority, isAsync);
        }

        public void RegisterCompositeCommand(Type[] signalTypes, Type commandType, bool oneShot, int priority, bool isAsync)
        {
            // Validation, the composite tables (all-triggers + by-signal, sorted by priority),
            // and the DI binding all live in the CommandRegistry — SignalBus only dispatches
            // against them via TryGetCompositeTriggers/ProcessCompositeTriggers.
            _commandRegistry.RegisterCompositeCommand(signalTypes, commandType, oneShot, priority, isAsync);
        }

        public void Fire<T>(T signal) where T : struct
        {
            FireInternal(signal, isCrossContextSource: false);
        }

        public async ValueTask FireAsync<T>(T signal) where T : struct
        {
            await FireInternalAsync(signal, isCrossContextSource: false);
        }

        private HybridQueue _cachedHybridQueue;
        private HybridQueue GetHybridQueue()
        {
            if (_cachedHybridQueue != null) return _cachedHybridQueue;
            _cachedHybridQueue = _container.Resolve<HybridQueue>();
            return _cachedHybridQueue;
        }

        public void FireThreadSafe<T>(T signal) where T : struct
        {
            GetHybridQueue().EnqueueThreadSafe(signal);
        }

        public void FireNextFrame<T>(T signal) where T : struct
        {
            GetHybridQueue().EnqueueNextFrame(signal);
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
            // Delegated to the SubscriptionRegistry — the single storage layer owns the pooled
            // node list, the volatile read copy, and the deferred sweep on dispatch unwind.
            return _subscriptionRegistry.Subscribe<T>(handler, _context.LifetimeToken);
        }

        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            return _subscriptionRegistry.SubscribeAsync<T>(handler, _context.LifetimeToken);
        }

        // Unsubscribe/SweepDeadNodes live in the SubscriptionRegistry (deferred sweep on
        // dispatch unwind so a pooled node is never reset while a reader walks it).

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
            bool hasAsync = _commandRegistry.HasAsyncCommandHandlers(type);
            bool hasAsyncSubscriptions = _subscriptionRegistry.HasAsyncSubscriptions(type);

            if (hasAsync || hasAsyncSubscriptions)
            {
                throw new NexusSyncAsyncMismatchException(
                    $"Synchronous Fire() was called for signal '{typeof(T).FullName}', but it has asynchronous handlers or subscriptions registered. " +
                    "To preserve sequential ordering and prevent race conditions, you must invoke this signal using FireAsync() and await its completion, or use FireAsyncAndForget().");
            }

            // === FAST PATH: All handlers are synchronous ===
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_subscriptionRegistry.SubscriptionsReadCopy.ContainsKey(type) && !_commandRegistry.TryGetHandlers(type, out _))
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Signal '{typeof(T).FullName}' fired but has no subscribers or command handlers registered. This may indicate a missing BindCommand or Subscribe call.");
            }
#endif
            s_stackDepth++;
            if (s_stackDepth > MaxStackDepth)
            {
                // P0-7 fix: never reset the counter to 0 (outer frames still decrement in
                // their finally blocks, which would drift the counter negative). This branch
                // runs before this frame's try/finally, so undo only this frame's increment.
                s_stackDepth--;
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
            _subscriptionRegistry.EnterDispatch();
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
                    var crossContextAttr = _commandRegistry.GetCachedCrossContext(type);
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
                if (_commandRegistry.TryGetHandlers(type, out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        ExecuteCommand(handler, signal);
                    }
                }

                // Phase 2: Process subscriptions (observe final state)
                if (_subscriptionRegistry.SubscriptionsReadCopy.TryGetValue(type, out var node))
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
                s_stackDepth--;
                _subscriptionRegistry.ExitDispatch();
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
            bool hasAsync = _commandRegistry.HasAsyncCommandHandlers(typeof(CommandFailedSignal))
                || _subscriptionRegistry.HasAsyncSubscriptions(typeof(CommandFailedSignal));
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


        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            await FireInternalAsync(signal, isCrossContextSource, _context.LifetimeToken);
        }

        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource, CancellationToken ct) where T : struct
        {
            s_asyncStackDepth.Value++;

            // Capture the command-scoped token for use in the nested scopes below.
            // This allows FireAsyncWithTimeout to cancel command execution via a linked token.
            var commandCt = ct;
            if (s_asyncStackDepth.Value > MaxStackDepth)
            {
                // P0-7 fix: never reset the counter to 0 (outer frames still decrement in
                // their finally blocks, which would drift the counter negative). This branch
                // runs before this frame's try/finally, so undo only this frame's increment.
                s_asyncStackDepth.Value--;
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
            _subscriptionRegistry.EnterDispatch();
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
                    var crossContextAttr = _commandRegistry.GetCachedCrossContext(type);
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // ═══ EXECUTION ORDER GUARANTEE (Async Path) ═══
                // Commands execute FIRST (they mutate model state),
                // then subscriptions execute AFTER (they observe final state).

                // Phase 1: Process commands (mutate state)
                if (_commandRegistry.TryGetHandlers(type, out var handlers))
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
                if (_subscriptionRegistry.SubscriptionsReadCopy.TryGetValue(type, out var node))
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
                s_asyncStackDepth.Value--;
                _subscriptionRegistry.ExitDispatch();
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
                            ExecuteDecoratedCommand(genericSyncCmd, signal);
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
                        var dispatcher = _commandRegistry.GetGenericSyncDispatcher(command.GetType(), signal.GetType());
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
                        if (_context is Context decoratorCtx && decoratorCtx.Plugins.Count > 0)
                        {
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
                        else
                        {
                            if (handler.TimeoutMs > 0)
                            {
                                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                timeoutCts.CancelAfter(handler.TimeoutMs);
                                await genericAsyncCmd.ExecuteAsync(signal, timeoutCts.Token);
                            }
                            else
                            {
                                await genericAsyncCmd.ExecuteAsync(signal, ct);
                            }
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
                        var asyncDispatcher = _commandRegistry.GetGenericAsyncDispatcher(command.GetType(), signal.GetType());
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
                            var syncDispatcher = _commandRegistry.GetGenericSyncDispatcher(command.GetType(), signal.GetType());
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
        /// This is only used in non-generic ICommand execution. The compiled setter cache lives in the
        /// CommandRegistry (shared with the standalone registry; the CR/DIFF suite proves parity).
        /// </summary>
        private void InjectSignal(object command, object signal)
        {
            if (signal == null) return;

            _commandRegistry.GetSignalSetter(command.GetType(), signal.GetType())(command, signal);
        }

        private void ProcessCompositeTriggers<T>(T signal) where T : struct
        {
            // P1-14 fix: collect due triggers under the registry's composite lock (snapshot copy),
            // then execute them OUTSIDE any lock so user command code never runs while holding one.
            var signalType = typeof(T);
            List<(CompositeTriggerState trigger, CompositeContext context)> dueTriggers = null;
            // Composite payload support: box the signal at most once, and only when it actually
            // feeds a registered composite trigger. Non-composite signals never allocate here.
            object boxedSignal = null;

            if (!_commandRegistry.TryGetCompositeTriggers(signalType, out var triggers))
                return;

            lock (_compositeLock)
            {
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

                // BUG-5 fix: use OrdinalIgnoreCase to match NexusRuntime.GetContext()
                // behaviour. The previous == comparison was case-sensitive, so a ScopeTag
                // mismatch like "Gameplay" vs "gameplay" would silently skip the target.
                if (!string.IsNullOrEmpty(scopeTag))
                {
                    if (string.Equals(targetCtx.ScopeTag, scopeTag, StringComparison.OrdinalIgnoreCase) &&
                        targetCtx.SignalBus is SignalBus concreteBus)
                    {
                        concreteBus.FireCrossContext(signal);
                    }
                }
                else
                {
                    if (targetCtx.SignalBus is SignalBus concreteBus)
                    {
                        concreteBus.FireCrossContext(signal);
                    }
                    else
                    {
                        Console.WriteLine($"[DEBUG-BROADCAST-FAIL] targetCtx={targetCtx.GetType().Name}, SignalBus={targetCtx.SignalBus?.GetType().Name ?? "null"}");
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
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void RunQueuedAsyncDispatch<T>(T signal) where T : struct
        {
            SafeAsyncRunner.Run(() => FireInternalAsync(signal, isCrossContextSource: false),
                $"Queued async dispatch failed for signal '{typeof(T).FullName}'");
        }

        internal void FireQueued<T>(T signal) where T : struct
        {
            bool hasAsync = _commandRegistry.HasAsyncCommandHandlers(typeof(T))
                || _subscriptionRegistry.HasAsyncSubscriptions(typeof(T));
            if (hasAsync)
            {
                RunQueuedAsyncDispatch(signal);
            }
            else
            {
                FireInternal(signal, isCrossContextSource: false);
            }
        }

        private RecoveryAction HandleCommandErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
            => HandleCommandErrorWithDecision<object>(ex, commandType, signal, ref retryCount);

        private RecoveryAction HandleCommandErrorWithDecision<TSignal>(Exception ex, Type commandType, TSignal signal, ref int retryCount)
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

        // Generic-only dispatcher/setter caches live in CommandRegistry (shared across every bus
        // and the standalone registry). SignalBus only dispatches through them — see
        // CommandRegistry.GetGenericSyncDispatcher / GetGenericAsyncDispatcher / GetSignalSetter.

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ExecuteDecoratedCommand<TSignal>(ICommand<TSignal> cmd, TSignal signal) where TSignal : struct
        {
            ExecuteWithDecorators(cmd, () => cmd.Execute(signal));
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
            // Snapshot the nodes before disposing: RawSubscription.Dispose() re-enters
            // the registry's Unsubscribe → deferred sweep. The registries then reclaim
            // every node and clear all state, so we dispose the raw subscriptions first
            // (their callbacks can no-op safely once the registries are emptied).
            List<SubscriptionNode> nodes = null;
            foreach (var kvp in _subscriptionRegistry.SubscriptionsReadCopy)
            {
                var current = kvp.Value;
                while (current != null)
                {
                    (nodes ??= new List<SubscriptionNode>()).Add(current);
                    current = current.Next;
                }
            }

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.IsActive && node.RawSubscription is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }

            _subscriptionRegistry.Dispose();
            _commandRegistry.Dispose();

            if (Volatile.Read(ref _inFlightAsyncCommands) > 0)
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] SignalBus disposed while {_inFlightAsyncCommands} async command(s) are still in-flight. This may cause unexpected behavior.");
            }
        }

        internal static void ClearStaticCaches()
        {
            CommandRegistry.ClearStaticCaches();
            SubscriptionNodePool.Clear();
            OnUnhandledException = null;
        }
    }

}
