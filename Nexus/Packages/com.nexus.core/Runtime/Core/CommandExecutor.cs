using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Unity.Profiling;

namespace Nexus.Core
{
    /// <summary>
    /// Owns command execution: the four dispatch loops (generic/object × sync/async),
    /// composite execution, decorator chaining, signal injection, and the in-flight
    /// async guard. SignalBus delegates every command invocation to this module so the
    /// retry/recovery/decorator/pool choreography lives in exactly one place (the harness
    /// differential suite proves the wired bus still behaves identically to the standalone
    /// registries).
    /// </summary>
    [Preserve]
    public sealed class CommandExecutor
    {
        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;
        private readonly CommandRegistry _commandRegistry;
        private readonly RecoveryEngine _recovery;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

        /// <summary>Number of async commands currently in flight (for dispose diagnostics).</summary>
        public int InFlightAsyncCommands => Volatile.Read(ref _inFlightAsyncCommands);

        /// <summary>T2 fix: signals cancellation to all in-flight async commands (via the context
        /// lifetime token). Called by SignalBus.Dispose() before tearing down registries.</summary>
        public void TryCancelInFlightCommands()
        {
            // The context lifetime token is already cancelled by the time Dispose reaches us
            // (Context.Dispose cancels _cts before disposing the SignalBus). Async commands
            // that check ct.IsCancellationRequested will observe cancellation and complete
            // their cleanup promptly. No additional action needed here — the cancellation
            // token propagation is the cancellation mechanism.
        }

        /// <summary>
        /// Enters the async in-flight guard. Throws <see cref="NexusAsyncOverflowException"/>
        /// (in ALL build targets) when the concurrent async command cap is exceeded. The
        /// pre-A9 Release branch logged and silently dropped the command while the caller's
        /// async chain completed "successfully" — the same silent state-corruption class the
        /// A8 reentrancy fix eliminated. Overflow is unrecoverable here: throwing everywhere
        /// lets RecoveryEngine triage and tests observe identical behavior.
        /// A successful call MUST be paired with an <see cref="Interlocked.Decrement"/> when
        /// the command finishes (the caller's finally does this via inFlightIncremented).
        /// </summary>
        private void EnterAsyncInFlight()
        {
            var count = Interlocked.Increment(ref _inFlightAsyncCommands);
            if (count > MaxInFlightAsyncCommands)
            {
                Interlocked.Decrement(ref _inFlightAsyncCommands);
                throw new NexusAsyncOverflowException($"Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
            }
        }

#if NEXUS_DEBUG
        private static readonly ProfilerMarker s_CommandMarker = new ProfilerMarker("Nexus.Command.Execute");
#endif

        public CommandExecutor(NexusDI container, CommandPoolManager poolManager, IContext context, CommandRegistry commandRegistry, RecoveryEngine recovery)
        {
            _container = container;
            _poolManager = poolManager;
            _context = context;
            _commandRegistry = commandRegistry;
            _recovery = recovery;
        }

        public void Execute<TSignal>(CommandHandlerInfo handler, TSignal signal) where TSignal : struct
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
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                        {
                            // Call via the [NoInlining] helper: an inline lambda here would capture
                            // 'signal' (referenced from the catch/finally below), which makes Roslyn
                            // hoist a closure display-class allocation to method entry — ~56 B per
                            // dispatch on the zero-GC hot path (proven via IL dump + alloc-diag).
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
                    var action = _recovery.HandleErrorWithDecision(ex, handler.CommandType, signal, ref retryCount);
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

        private async ValueTask ExecuteAsyncWithOptionalTimeout(IAsyncCommand asyncCmd, int timeoutMs, CancellationToken ct, bool useDecorators)
        {
            if (timeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                ct = timeoutCts.Token;
            }

            if (useDecorators && _context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
            {
                await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
            }
            else
            {
                await asyncCmd.ExecuteAsync(ct);
            }
        }

        private async ValueTask ExecuteGenericAsyncWithOptionalTimeout<TSignal>(IAsyncCommand<TSignal> asyncCmd, TSignal signal, int timeoutMs, CancellationToken ct, bool useDecorators) where TSignal : struct
        {
            if (timeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                ct = timeoutCts.Token;
            }

            if (useDecorators && _context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
            {
                await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(signal, ct));
            }
            else
            {
                await asyncCmd.ExecuteAsync(signal, ct);
            }
        }

        private async ValueTask ExecuteAsyncDispatcherWithOptionalTimeout(object command, Func<object, object, CancellationToken, ValueTask> asyncDispatcher, object signal, int timeoutMs, CancellationToken ct, bool useDecorators)
        {
            if (timeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                ct = timeoutCts.Token;
            }

            if (useDecorators && _context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
            {
                await ExecuteDecoratedAsyncDispatcher(command, asyncDispatcher, signal, ct);
            }
            else
            {
                await asyncDispatcher(command, signal, ct);
            }
        }

        public void Execute(CommandHandlerInfo handler, object signal)
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
                    InjectSignal(command, signal);

                    if (command is ICommand syncCmd)
                    {
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
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
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                        {
                            // [NoInlining] helper: an inline lambda here captures 'command' (used
                            // in the finally below) and 'signal' (used in the catch), which would
                            // make Roslyn hoist the closure display-class to method entry and
                            // allocate on EVERY object dispatch even without decorators.
                            ExecuteDecoratedDispatcher(command, dispatcher, signal);
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
                    var action = _recovery.HandleErrorWithDecision(ex, handler.CommandType, signal, ref retryCount);
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

        public async ValueTask ExecuteAsync<TSignal>(CommandHandlerInfo handler, TSignal signal, CancellationToken ct) where TSignal : struct
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
                bool inFlightIncremented = false;
                try
                {
                    EnterAsyncInFlight();
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);

                    if (command is IAsyncCommand<TSignal> genericAsyncCmd)
                    {
                        await ExecuteGenericAsyncWithOptionalTimeout(genericAsyncCmd, signal, handler.TimeoutMs, ct, useDecorators: _context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0);
                    }
                    else if (command is ICommand<TSignal> genericSyncCmd)
                    {
                        // A sync command executed in the async dispatch path must still
                        // honour the cancellation token so a timeout or teardown does not
                        // stall the pipeline.
                        ct.ThrowIfCancellationRequested();
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                        {
                            // Same [NoInlining] helper as the sync path: an inline lambda here
                            // would capture 'signal' (referenced from the catch below), which
                            // hoists a closure display-class to method entry — allocating on
                            // every async dispatch of a sync command even without decorators.
                            ExecuteDecoratedCommand(genericSyncCmd, signal);
                        }
                        else
                        {
                            genericSyncCmd.Execute(signal);
                        }
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
                    var action = await _recovery.HandleErrorWithDecisionAsync(ex, handler.CommandType, signal, retryCount, ct);
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

        public async ValueTask ExecuteAsync(CommandHandlerInfo handler, object signal, CancellationToken ct)
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
                bool inFlightIncremented = false;
                try
                {
                    EnterAsyncInFlight();
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    InjectSignal(command, signal);

                    if (command is IAsyncCommand asyncCmd)
                    {
                        // P0-5 fix: apply [CommandTimeout] via a linked, self-cancelling token.
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                        {
                            await ExecuteAsyncWithOptionalTimeout(asyncCmd, handler.TimeoutMs, ct, useDecorators: true);
                        }
                        else
                        {
                            await ExecuteAsyncWithOptionalTimeout(asyncCmd, handler.TimeoutMs, ct, useDecorators: false);
                        }
                    }
                    else if (command is ICommand syncCmd)
                    {
                        if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
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
                            if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                            {
                                await ExecuteAsyncDispatcherWithOptionalTimeout(command, asyncDispatcher, signal, handler.TimeoutMs, ct, useDecorators: true);
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
                            if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
                            {
                                // [NoInlining] helper — same closure-isolation as the sync object path.
                                ExecuteDecoratedDispatcher(command, syncDispatcher, signal);
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
                    var action = await _recovery.HandleErrorWithDecisionAsync(ex, handler.CommandType, signal, retryCount, ct);
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
        internal void InjectSignal(object command, object signal)
        {
            if (signal == null) return;

            var setter = _commandRegistry.GetSignalSetter(command.GetType(), signal.GetType());
            if (setter == null) return;
            setter(command, signal);
        }

        // ─── Composite execution ───────────────────────────────────────────────

        private async ValueTask ExecuteCompositeCommandAsyncCore(CompositeTriggerState trigger, object command, CompositeContext context)
        {
            int retryCount = 0;
            bool shouldRun = true;
            // NOTE: metrics are recorded in ExecuteComposite (the single public entry point)
            // — async composites flow through here via ExecuteCompositeCommandAsync, so
            // recording here too would double-count every async composite command.

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

                    // Gated like every other dispatch surface: the tail closures below
                    // allocate, so they are only created when decorators are registered
                    // (ExecuteWithDecorators internally no-ops to `next()` otherwise).
                    bool hasDecorators = _context is Context decoratorContext && decoratorContext.PluginsReadOnlyCopy.Count > 0;
                    if (command is ICompositeCommand syncCompCmd)
                    {
                        if (hasDecorators)
                        {
                            ExecuteWithDecorators(syncCompCmd, () => syncCompCmd.Execute(context));
                        }
                        else
                        {
                            syncCompCmd.Execute(context);
                        }
                    }
                    else if (command is ICommand syncCmd)
                    {
                        if (hasDecorators)
                        {
                            ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                        }
                        else
                        {
                            syncCmd.Execute();
                        }
                    }
                    else if (command is IAsyncCompositeCommand asyncCompCmd)
                    {
                        var ct = _context?.LifetimeToken ?? CancellationToken.None;
                        // A9 fix: composite async commands share the same in-flight cap as
                        // regular async commands — overflow aborts the pipeline everywhere.
                        EnterAsyncInFlight();
                        inFlightIncremented = true;
                        try
                        {
                            if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
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
                        // A9 fix: composite async commands share the same in-flight cap as
                        // regular async commands — overflow aborts the pipeline everywhere.
                        EnterAsyncInFlight();
                        inFlightIncremented = true;
                        try
                        {
                            if (_context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0)
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
                    RecoveryAction action;
                    try
                    {
                        action = _recovery.HandleErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
                    }
                    catch
                    {
                        // M2 fix: HandleErrorWithDecision can THROW (strategy Abort, retry-limit
                        // reached, or ExceptionDispatchInfo rethrow of OCE/Reentrancy). When it
                        // does, shouldRun stays true and the finally below would skip the
                        // ReturnCommand, leaking the pooled command and leaving the pool entry
                        // stuck. Mark the loop as exiting so the command is returned to the
                        // pool before the recovery exception propagates to the caller.
                        shouldRun = false;
                        throw;
                    }
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
                    // M2 fix: return the pooled command on EVERY exit path — success,
                    // skip/abort action, AND recovery rethrow (shouldRun set false in the
                    // catch above). Only the Retry loop iteration keeps it rented.
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

        public void ExecuteComposite(CompositeTriggerState trigger, CompositeContext context)
        {
            int retryCount = 0;
            bool shouldRun = true;

            // Single public composite entry point (SignalBus dispatches composites only here):
            // record once per composite command — sync composites execute inline, async
            // composites delegate to ExecuteCompositeCommandAsyncCore below (no re-record).
            NexusRuntime.Metrics.RecordCommandExecuted();
            NexusRuntime.Metrics.RecordTrace(trigger.CommandType.Name);

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
                    bool hasDecorators = _context is Context decoratorCtx && decoratorCtx.PluginsReadOnlyCopy.Count > 0;

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
                        try
                        {
                            ExecuteCompositeCommandAsync(trigger, cmdForAsync, context);
                            command = null; // Prevent finally from returning it; async method owns it now
                            shouldRun = false;
                            return;
                        }
                        catch
                        {
                            _poolManager.ReturnCommand(trigger.CommandType, cmdForAsync);
                            throw;
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
                    var action = _recovery.HandleErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
                    if (action != RecoveryAction.Retry)
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

        // ─── Decorator chaining ─────────────────────────────────────────────────

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ExecuteDecoratedCommand<TSignal>(ICommand<TSignal> cmd, TSignal signal) where TSignal : struct
        {
            ExecuteWithDecorators(cmd, () => cmd.Execute(signal));
        }

        /// <summary>
        /// [NoInlining] wrapper for the object-path generic-only dispatcher: keeps the
        /// closure (which must capture 'command' + 'signal' — both referenced from this
        /// caller's finally/catch) out of the dispatch method, so Roslyn does not hoist
        /// a display-class allocation to method entry on the no-decorator hot path.
        /// Only called when decorators are registered.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ExecuteDecoratedDispatcher(object command, Action<object, object> dispatcher, object signal)
        {
            ExecuteWithDecorators(command, () => dispatcher(command, signal));
        }

        /// <summary>
        /// [NoInlining] wrapper for the object-path generic-only async dispatcher — same
        /// closure-isolation rationale as <see cref="ExecuteDecoratedDispatcher"/>, for the
        /// async overload's generic-only branch. Only called when decorators are registered.
        /// Returns the ValueTask directly (no extra async state machine): the composed
        /// decorator chain is still fully awaited by the caller.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private ValueTask ExecuteDecoratedAsyncDispatcher(object command, Func<object, object, CancellationToken, ValueTask> dispatcher, object signal, CancellationToken ct)
        {
            return ExecuteWithDecoratorsAsync(command, () => dispatcher(command, signal, ct));
        }

        // Cached flattened decorator chains, keyed by the plugins snapshot list reference.
        // Context swaps _pluginsReadOnlyCopy for a NEW List on every AddPlugin/RemovePlugin,
        // so the key changes whenever the plugin set changes. PluginContext additionally
        // swaps its per-plugin Decorators snapshot for a new List on EVERY
        // RegisterCommandDecorator, so each cache entry captures those snapshot references
        // and RE-VALIDATES them by reference on lookup — a decorator registered at any time
        // (including inside OnPluginRegistered, which runs after the context's snapshot
        // rebuild) invalidates the chain with no explicit invalidation protocol. This
        // eliminates the per-dispatch List build + plugin iteration + AddRange on the
        // decorated path; chains are stored in EXECUTION order (outermost first). Entries
        // for superseded plugin snapshots are retained (bounded by plugin churn, which is
        // rare — plugins register once at context bootstrap).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<object, DecoratorChainEntry> _decoratorChainCache = new();

        private sealed class DecoratorChainEntry
        {
            public IReadOnlyList<ICommandDecorator>[] PluginDecoratorSnapshots; // per-plugin, reference-validated
            public ICommandDecorator[] Chain; // execution order, outermost first
        }

        private ICommandDecorator[] GetDecoratorChain(Context ctx)
        {
            var snapshot = ctx.PluginsReadOnlyCopy;
            if (_decoratorChainCache.TryGetValue(snapshot, out var cached))
            {
                bool valid = snapshot.Count == cached.PluginDecoratorSnapshots.Length;
                for (int i = 0; valid && i < snapshot.Count; i++)
                {
                    // RegisterCommandDecorator swaps _decoratorsSnapshot for a new List on
                    // every mutation — reference inequality means the cached chain is stale.
                    valid = ReferenceEquals(snapshot[i].context.Decorators, cached.PluginDecoratorSnapshots[i]);
                }
                if (valid) return cached.Chain;
            }

            // Rebuild: plugins backward, decorators within each plugin backward →
            // execution order (outermost = last plugin's last decorator).
            var chain = new List<ICommandDecorator>();
            var snapshots = new IReadOnlyList<ICommandDecorator>[snapshot.Count];
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                var decorators = snapshot[i].context.Decorators;
                snapshots[i] = decorators;
                for (int j = decorators.Count - 1; j >= 0; j--)
                {
                    chain.Add(decorators[j]);
                }
            }
            var entry = new DecoratorChainEntry { PluginDecoratorSnapshots = snapshots, Chain = chain.ToArray() };
            _decoratorChainCache[snapshot] = entry;
            return entry.Chain;
        }

        internal void ExecuteWithDecorators(object command, Action next)
        {
            if (_context is Context ctx && ctx.PluginsReadOnlyCopy.Count > 0)
            {
                var chain = GetDecoratorChain(ctx);
                if (chain.Length > 0)
                {
                    // Closure-free chain runner: the per-level closure allocations are
                    // collapsed into ONE reusable mutable runner (rented from a ThreadStatic
                    // free list) that walks the cached chain, passing its own pre-created
                    // delegate as `next`. The only remaining per-dispatch allocation is the
                    // caller's tail closure, which must capture the pooled command instance.
                    var runner = RentDecoratorRunner();
                    try { runner.Run(command, next, chain); }
                    finally { ReturnDecoratorRunner(runner); }
                    return;
                }
            }
            next();
        }

        internal async ValueTask ExecuteWithDecoratorsAsync(object command, Func<ValueTask> next)
        {
            if (_context is Context ctx && ctx.PluginsReadOnlyCopy.Count > 0)
            {
                var chain = GetDecoratorChain(ctx);
                if (chain.Length > 0)
                {
                    // Async chains cannot use the pooled runner (their state must survive
                    // awaits and could be clobbered by another dispatch resuming on the same
                    // thread), so the per-level async lambdas remain — but they are composed
                    // from the cached chain instead of a per-dispatch List build + plugin
                    // iteration, and the chain order matches the sync runner exactly.
                    Func<ValueTask> current = next;
                    for (int i = chain.Length - 1; i >= 0; i--)
                    {
                        var d = chain[i];
                        var prev = current;
                        current = async () => await d.DecorateExecuteAsync(command, prev);
                    }
                    await current();
                    return;
                }
            }
            await next();
        }

        // ─── Decorator runner (closure-free, ThreadStatic-pooled) ──────────────
        // CONTRACT: the runner assumes a SYNCHRONOUS, SINGLE `next()` invocation per
        // decorator (the ICommandDecorator.DecorateExecute signature is void and
        // synchronous). A decorator that defers `next()` past its DecorateExecute return
        // — or invokes it multiple times — is outside the contract: the deferred call
        // would run after the runner was returned to the pool (and possibly re-rented by
        // another dispatch on this thread), and a double call would skip the intermediate
        // wraps (the old per-execution closure chain re-ran them). Both patterns are
        // broken under the old composition too (out-of-order/duplicated execution); the
        // pooled runner just surfaces the violation differently. All in-repo decorators
        // and the harness suites call `next()` synchronously and exactly once.

        [ThreadStatic]
        private static DecoratorRunner s_runnerFreeList;

        private sealed class DecoratorRunner
        {
            private readonly Action _step;
            public object Command;
            public Action Next;
            public ICommandDecorator[] Chain;
            public int Index;
            public DecoratorRunner NextFree;

            public DecoratorRunner()
            {
                // Create the delegate ONCE per runner so walking the chain never allocates
                // a per-level delegate (a per-invocation method-group conversion would).
                _step = Step;
            }

            public void Run(object command, Action next, ICommandDecorator[] chain)
            {
                Command = command;
                Next = next;
                Chain = chain;
                Index = 0;
                Step();
            }

            private void Step()
            {
                if (Index < Chain.Length)
                {
                    var d = Chain[Index++];
                    d.DecorateExecute(Command, _step);
                }
                else
                {
                    Next();
                }
            }
        }

        private static DecoratorRunner RentDecoratorRunner()
        {
            var r = s_runnerFreeList;
            if (r != null)
            {
                s_runnerFreeList = r.NextFree;
                r.NextFree = null;
                return r;
            }
            return new DecoratorRunner();
        }

        private static void ReturnDecoratorRunner(DecoratorRunner r)
        {
            r.Command = null;
            r.Next = null;
            r.Chain = null;
            r.NextFree = s_runnerFreeList;
            s_runnerFreeList = r;
        }
    }
}
