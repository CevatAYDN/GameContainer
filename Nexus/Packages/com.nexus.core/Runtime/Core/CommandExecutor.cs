using System;
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

        public void Execute(CommandHandlerInfo handler, object signal)
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
                        // A sync command executed in the async dispatch path must still
                        // honour the cancellation token so a timeout or teardown does not
                        // stall the pipeline.
                        ct.ThrowIfCancellationRequested();
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

            _commandRegistry.GetSignalSetter(command.GetType(), signal.GetType())(command, signal);
        }

        // ─── Composite execution ───────────────────────────────────────────────

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
                        // A9 fix: composite async commands share the same in-flight cap as
                        // regular async commands — overflow aborts the pipeline everywhere.
                        EnterAsyncInFlight();
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
                        // A9 fix: composite async commands share the same in-flight cap as
                        // regular async commands — overflow aborts the pipeline everywhere.
                        EnterAsyncInFlight();
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
                    var action = _recovery.HandleErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
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

        public void ExecuteComposite(CompositeTriggerState trigger, CompositeContext context)
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
                    var action = _recovery.HandleErrorWithDecision(ex, trigger.CommandType, null, ref retryCount);
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

        // ─── Decorator chaining ─────────────────────────────────────────────────

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ExecuteDecoratedCommand<TSignal>(ICommand<TSignal> cmd, TSignal signal) where TSignal : struct
        {
            ExecuteWithDecorators(cmd, () => cmd.Execute(signal));
        }

        internal void ExecuteWithDecorators(object command, Action next)
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

        internal async ValueTask ExecuteWithDecoratorsAsync(object command, Func<ValueTask> next)
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
    }
}
