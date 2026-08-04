using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Unity.Profiling;

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
            try
            {
                ValueTask task = func();
                if (task.IsCompleted)
                {
                    if (task.IsFaulted)
                    {
                        Exception ex = task.AsTask().Exception?.InnerException;
                        if (ex != null && !(ex is OperationCanceledException))
                        {
                            SignalBus.RaiseUnhandledException(ex, errorContext);
                            NexusRuntime.Logger?.LogError($"[Nexus] {errorContext}: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    return;
                }
                _ = RunAsync(task, errorContext);
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                {
                    SignalBus.RaiseUnhandledException(ex, errorContext);
                    NexusRuntime.Logger?.LogError($"[Nexus] {errorContext} (sync throw): {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private static async Task RunAsync(ValueTask task, string errorContext)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected during context cancellation
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

        // ─── Execution & recovery modules ───
        // The four command-execution loops (generic/object × sync/async), composite
        // execution, decorator chaining, signal injection, and the in-flight async guard
        // live in CommandExecutor; the failure-decision policy (strategy resolution,
        // fallback validation, retry accounting, failed-signal dispatch) lives in
        // RecoveryEngine. SignalBus dispatches and delegates — the harness differential
        // suite proves the wired bus behaves identically to the standalone registries.
        private readonly CommandExecutor _commandExecutor;
        private readonly RecoveryEngine _recoveryEngine;

        public static event Action<Exception, string> OnUnhandledException;

        internal static void RaiseUnhandledException(Exception ex, string context)
        {
            OnUnhandledException?.Invoke(ex, context);
        }

        private volatile bool _disposed;
        private readonly NexusDI _container;
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

        // Shared reflection caches (signal setters, generic dispatchers, cross-context
        // attributes) live in CommandRegistry so every bus and the standalone registry share
        // ONE cache; cleared via CommandRegistry.ClearStaticCaches().

#if NEXUS_DEBUG
        private static readonly ProfilerMarker s_DispatchMarker = new ProfilerMarker("Nexus.Signal.Dispatch");
#endif

        public SignalBus(NexusDI container, CommandPoolManager poolManager, IContext context)
            : this(container, poolManager, context, null)
        {
        }

        public SignalBus(NexusDI container, CommandPoolManager poolManager, IContext context, IContextResolver contextResolver)
        {
            _container = container;
            _context = context;
            _contextResolver = contextResolver ?? NexusRuntime.DefaultContextResolver;
            _commandRegistry = new CommandRegistry(container);
            _subscriptionRegistry = new SubscriptionRegistry();
            // Restore the pre-refactor SignalBus semantics: an Unsubscribe while the bus is NOT
            // dispatching reclaims the node immediately; during dispatch it defers to unwind.
            _subscriptionRegistry.ImmediateSweepWhenIdle = true;

            // Recovery first (it needs the bus's failed-signal dispatch), then the executor
            // (it needs the recovery engine), then attach the executor to the engine so
            // fallback commands can execute through the real dispatch path.
            _recoveryEngine = new RecoveryEngine(container, FireFailedSignalSafe, async fs => await FireInternalAsync(fs, isCrossContextSource: false));
            _commandExecutor = new CommandExecutor(container, poolManager, context, _commandRegistry, _recoveryEngine);
            _recoveryEngine.AttachExecutor(_commandExecutor);
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

        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync, bool oneShot = false)
        {
            // Registration, validation, snapshot rebuild, async-handler tracking, and DI binding
            // all live in the CommandRegistry — SignalBus only dispatches against the registry.
            _commandRegistry.RegisterCommand(signalType, commandType, mode, priority, isAsync, oneShot);
        }

        /// <summary>
        /// True when <paramref name="type"/> implements a supported command interface
        /// (non-generic or generic sync/async, composite or plain). Shared by the assembly
        /// scan and the test harness so classification lives in exactly one place.
        /// </summary>
        internal static bool IsCommandType(Type type)
        {
            return typeof(ICommand).IsAssignableFrom(type) || ImplementsGenericInterface(type, typeof(ICommand<>))
                || typeof(IAsyncCommand).IsAssignableFrom(type) || ImplementsGenericInterface(type, typeof(IAsyncCommand<>))
                || typeof(ICompositeCommand).IsAssignableFrom(type) || typeof(IAsyncCompositeCommand).IsAssignableFrom(type);
        }

        /// <summary>
        /// Registers a command type's <c>[SignalHandler]</c>/<c>[CompositeSignalHandler]</c>
        /// attributes against this bus — the production registration path used by both the
        /// assembly scan and the test harness, so attribute parsing is never re-implemented.
        /// </summary>
        /// <param name="commandType">The command type (already bound in the container).</param>
        /// <param name="handlers">Pre-scanned <c>[SignalHandler]</c> attributes (may be empty).</param>
        /// <param name="composite">Pre-scanned <c>[CompositeSignalHandler]</c> attribute (or null).</param>
        /// <param name="forceAsync">
        /// When non-null, forces the sync/async flag on every registration (used by the
        /// harness's RegisterCommand/RegisterAsyncCommand). When null, the flag is derived
        /// from the command type exactly like the assembly scan does.
        /// </param>
        /// <returns>True when at least one handler was registered for a command type.</returns>
        internal bool RegisterCommandType(Type commandType, IEnumerable<SignalHandlerAttribute> handlers,
            CompositeSignalHandlerAttribute composite, bool? forceAsync = null)
        {
            bool isSync = typeof(ICommand).IsAssignableFrom(commandType) || ImplementsGenericInterface(commandType, typeof(ICommand<>));
            bool isAsync = typeof(IAsyncCommand).IsAssignableFrom(commandType) || ImplementsGenericInterface(commandType, typeof(IAsyncCommand<>));
            bool isCompositeSync = typeof(ICompositeCommand).IsAssignableFrom(commandType);
            bool isCompositeAsync = typeof(IAsyncCompositeCommand).IsAssignableFrom(commandType);

            if (!(isSync || isAsync || isCompositeSync || isCompositeAsync))
                return false;

            bool registered = false;
            foreach (var attr in handlers)
            {
                RegisterCommand(attr.SignalType, commandType, attr.Mode, attr.Priority, isAsync: forceAsync ?? (isAsync && !isSync));
                registered = true;
            }

            if (composite != null)
            {
                bool compositeIsAsync = forceAsync ?? ((isCompositeAsync && !isCompositeSync) || (isAsync && !isSync));
                RegisterCompositeCommand(composite.SignalTypes, commandType, composite.OneShot, composite.Priority, compositeIsAsync);
                registered = true;
            }

            return registered;
        }

        /// <summary>Returns true when at least one command handler is registered for the signal type.</summary>
        public bool HasCommandHandler(Type signalType)
            => _commandRegistry.TryGetHandlers(signalType, out var handlers) && handlers.Count > 0;

        /// <summary>Generic form of <see cref="HasCommandHandler(Type)"/>.</summary>
        public bool HasCommandHandler<TSignal>() where TSignal : struct
            => HasCommandHandler(typeof(TSignal));

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
                    try { onError(ex); }
                    catch (Exception handlerEx) { NexusRuntime.Logger?.LogException(handlerEx); }
                }
                else
                {
                    try { OnUnhandledException?.Invoke(ex, $"FireAsyncAndForget failed for signal '{typeof(T).FullName}'"); }
                    catch (Exception handlerEx) { NexusRuntime.Logger?.LogException(handlerEx); }
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
                // A8 fix: reentrancy protection must throw in ALL build targets. Silently
                // returning in Release builds hid the state corruption that a runaway
                // signal chain would cause — the guard now aborts the chain everywhere so
                // recovery (RecoveryEngine triage) and tests see the same behavior.
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
            }

#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
            s_DispatchMarker.Begin();
#endif
            _subscriptionRegistry.EnterDispatch();
            try
            {
                // Run plugins' SignalInterceptors
                // P0-CR fix: defer boxing until we find a non-empty interceptor list.
                // When no interceptors are registered (the common case), the struct is
                // never copied to the heap, eliminating the per-Fire() GC allocation.
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.HasInterceptors)
                {
                    object boxedSignal = null;
                    var plugins = ctx.PluginsReadOnlyCopy;
                    for (int i = 0; i < plugins.Count; i++)
                    {
                        var interceptors = plugins[i].context.Interceptors;
                        if (interceptors.Count == 0) continue;
                        boxedSignal ??= (object)signal; // box only on first actual interceptor
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
                    if (boxedSignal != null)
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
                    // One-shot handlers are claimed atomically BEFORE execution: the winning
                    // fire executes, concurrent fires that observe the same read-copy snapshot
                    // lose the claim and skip — guaranteeing exactly-once even under races.
                    foreach (var handler in handlers)
                    {
                        if (handler.IsOneShot && !_commandRegistry.TryClaimOneShot(type, handler.CommandType))
                            continue; // another fire already claimed this one-shot handler
                        _commandExecutor.Execute(handler, signal);
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
                try
                {
                    FireInternal(failedSignal, isCrossContextSource: false);
                }
                catch (Exception ex)
                {
                    RaiseUnhandledException(ex, "CommandFailedSignal sync dispatch failed");
                    NexusRuntime.Logger?.LogError($"[Nexus] CommandFailedSignal sync dispatch failed: {ex.Message}\n{ex.StackTrace}");
                }
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
                // A8 fix: same as the sync path — always throw, never return silently in
                // Release builds (silent return masked runaway async chains).
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
            }

#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
#endif
            _subscriptionRegistry.EnterDispatch();
            try
            {
                var type = typeof(T);

                // Run plugins' SignalInterceptors
                // P0-CR fix: defer boxing until we find a non-empty interceptor list (async path).
                bool interceptorCancelled = false;
                if (_context is Context ctx && ctx.HasInterceptors)
                {
                    object boxedSignal = null;
                    var plugins = ctx.PluginsReadOnlyCopy;
                    for (int i = 0; i < plugins.Count; i++)
                    {
                        var interceptors = plugins[i].context.Interceptors;
                        if (interceptors.Count == 0) continue;
                        boxedSignal ??= (object)signal;
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
                    if (boxedSignal != null)
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
                        // One-shot handlers are claimed atomically BEFORE any task starts so a
                        // concurrent fire can never double-execute them. Claiming first also
                        // guarantees the async one-shot is consumed synchronously before the
                        // first await, closing the race where a second fire slips in while the
                        // first command is still pending.
                        var toRun = handlers;
                        bool anyOneShot = false;
                        for (int i = 0; i < handlers.Count && !anyOneShot; i++)
                            anyOneShot = handlers[i].IsOneShot;

                        if (anyOneShot)
                        {
                            // Build a fresh runnable list: a one-shot that a concurrent fire
                            // already claimed must be dropped, so falling back to the original
                            // snapshot (which still contains it) would double-execute.
                            toRun = new List<CommandHandlerInfo>(handlers.Count);
                            for (int i = 0; i < handlers.Count; i++)
                            {
                                var handler = handlers[i];
                                if (handler.IsOneShot && !_commandRegistry.TryClaimOneShot(type, handler.CommandType))
                                    continue; // already claimed by a concurrent fire — skip
                                toRun.Add(handler);
                            }
                        }

                        // Run concurrently
                        int taskCount = toRun.Count;
                        var tasks = System.Buffers.ArrayPool<ValueTask>.Shared.Rent(taskCount);
                        int started = 0;
                        int lastCompletedIndex = -1;
                        try
                        {
                            // A5: track how many tasks actually started. If ExecuteAsync throws
                            // synchronously mid-batch, the already-started ValueTasks must still
                            // be awaited — otherwise they are abandoned (their exceptions become
                            // unobserved and their work is silently lost).
                            for (; started < taskCount; started++)
                            {
                                tasks[started] = _commandExecutor.ExecuteAsync(toRun[started], signal, commandCt);
                            }

                            for (int i = 0; i < started; i++)
                            {
                                await tasks[i];
                                lastCompletedIndex = i;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Drain only the tasks that were started but not yet awaited before the failure
                            // so none is abandoned; swallow their individual errors (the original exception
                            // below is the one that propagates to recovery/error handling).
                        for (int i = lastCompletedIndex + 1; i < started; i++)
                        {
                            try { await tasks[i]; }
                            catch (Exception drainedEx)
                            {
                                ErrorCollection.CollectException(drainedEx, ErrorCollection.ErrorCategory.Signal,
                                    $"Concurrent signal handler drain failed for {typeof(T).FullName} at index {i}", logToConsole: true);
                                NexusRuntime.Logger?.LogError($"[Nexus] Concurrent signal handler drain failed for '{typeof(T).FullName}' at index {i}: {drainedEx.Message}\n{drainedEx.StackTrace}");
                            }
                        }
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                        throw; // unreachable — keeps the compiler happy
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<ValueTask>.Shared.Return(tasks);
                        }
                    }
                    else
                    {
                        // Run sequentially. One-shot handlers are claimed atomically BEFORE their
                        // single run so concurrent fires can never double-execute them; the async
                        // one-shot is consumed synchronously before its first await.
                        foreach (var handler in handlers)
                        {
                            if (handler.IsOneShot && !_commandRegistry.TryClaimOneShot(type, handler.CommandType))
                                continue; // another fire already claimed this one-shot handler
                            if (handler.IsAsync)
                            {
                                await _commandExecutor.ExecuteAsync(handler, signal, commandCt);
                            }
                            else
                            {
                                _commandExecutor.Execute(handler, signal);
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

        // P0-CR fix: thread-static pooled list for composite trigger dispatch — eliminates
        // per-Fire() heap allocation when composite triggers complete. Re-entrancy-safe:
        // a composite command that fires another signal completing another trigger on the
        // SAME thread must not clobber the outer call's pending buffer (C-1 fix), so the
        // nested call uses a fresh local list keyed off s_compositeDepth instead.
        [ThreadStatic] private static List<(CompositeTriggerState trigger, CompositeContext context)> s_dueTriggerBuffer;
        [ThreadStatic] private static int s_compositeDepth;

        private void ProcessCompositeTriggers<T>(T signal) where T : struct
        {
            // P1-14 fix: collect due triggers under the registry's composite lock (snapshot copy),
            // then execute them OUTSIDE any lock so user command code never runs while holding one.
            var signalType = typeof(T);

            // C-1 fix: when already processing composites on this thread (a composite command
            // fired another signal that completed another trigger), the shared ThreadStatic
            // buffer is the OUTER frame's list. The nested call must not Clear() it (that would
            // lose the outer pending triggers) nor append into it (that would double-execute
            // the nested entries from the outer loop). Use a per-frame local list instead.
            bool isNested = s_compositeDepth > 0;
            var buffer = isNested
                ? new List<(CompositeTriggerState trigger, CompositeContext context)>()
                : (s_dueTriggerBuffer ??= new List<(CompositeTriggerState trigger, CompositeContext context)>());
            if (!isNested) buffer.Clear();

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
                            buffer.Add((trigger, context));

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

            if (buffer.Count == 0) return;
            s_compositeDepth++;
            try
            {
                for (int i = 0; i < buffer.Count; i++)
                {
                    _commandExecutor.ExecuteComposite(buffer[i].trigger, buffer[i].context);
                }
            }
            finally
            {
                s_compositeDepth--;
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
                if (!string.IsNullOrEmpty(scopeTag) &&
                    !string.Equals(targetCtx.ScopeTag, scopeTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (targetCtx.SignalBus is SignalBus concreteBus)
                {
                    // T7 fix: the TARGET bus may have async handlers/subscriptions for this
                    // signal. The sync FireCrossContext would throw
                    // NexusSyncAsyncMismatchException and break the broadcasting dispatch.
                    // Route through the async fire-and-forget path (with error capture) when
                    // the target has async handlers, mirroring FireFailedSignalSafe.
                    // NOTE: the async lambda lives in a separate [NoInlining] method
                    // (RunCrossContextAsyncDispatch) so the lambda's closure/string cannot
                    // disturb JIT tiering on this hot path — the ZeroGC cross-context test
                    // measures exactly 0 bytes with this shape.
                    bool targetHasAsync = concreteBus._commandRegistry.HasAsyncCommandHandlers(typeof(T))
                        || concreteBus._subscriptionRegistry.HasAsyncSubscriptions(typeof(T));
                    if (targetHasAsync)
                    {
                        concreteBus.RunCrossContextAsyncDispatch(signal);
                    }
                    else
                    {
                        concreteBus.FireCrossContext(signal);
                    }
                }
                else if (string.IsNullOrEmpty(scopeTag))
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] Cross-context broadcast failed: target context '{targetCtx.GetType().Name}' does not use a SignalBus-backed ISignalBus. Broadcast skipped.");
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

        /// <summary>
        /// T7: fire-and-forget async dispatch used by <see cref="BroadcastCrossContext"/> when
        /// the TARGET bus has async handlers/subscriptions for the broadcast signal. Kept in a
        /// separate [NoInlining] method so the lambda closure and interpolated error string live
        /// off the cross-context hot path (which must stay allocation-free — the ZeroGC harness
        /// test measures 0 bytes per broadcast).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void RunCrossContextAsyncDispatch<T>(T signal) where T : struct
        {
            SafeAsyncRunner.Run(() => FireInternalAsync(signal, isCrossContextSource: true),
                $"Cross-context async dispatch failed for signal '{typeof(T).FullName}'");
        }

        /// <summary>
        /// Dispatches a signal from a queued/replay context (HybridQueue drain, network
        /// replay) without throwing NexusSyncAsyncMismatchException when the signal has
        /// async handlers.
        /// B8 contract: ordering is guaranteed only for sync-only signals. When the
        /// signal has async handlers, dispatch is fire-and-forget (async ordering is
        /// best-effort). Network replay is deterministic for sync signals; async replay
        /// signals should be awaited by the caller for strict ordering.
        /// </summary>
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
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

            // T2 fix: cancel in-flight async commands before disposal.
            // In-flight async commands continue running on disposed registries and container,
            // causing ObjectDisposedException/NullReferenceException. Pooled command objects
            // are also never returned (pool leak). Cancel them so they complete promptly.
            if (_commandExecutor.InFlightAsyncCommands > 0)
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] SignalBus disposed while {_commandExecutor.InFlightAsyncCommands} async command(s) are still in-flight. Attempting cancellation.");
                _commandExecutor.TryCancelInFlightCommands();
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
