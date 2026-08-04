using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Owns the command-failure recovery decision: exception triage, IRecoveryStrategy
    /// resolution, fallback-type validation, retry accounting, and failed-signal
    /// dispatch. Extracted from SignalBus so the recovery policy lives behind a seam —
    /// the harness RecoveryRegression suite exercises the exact decision paths at
    /// runtime (sync fallback dispatch, retry counting, async-only fallback rejection).
    /// Failed-signal dispatch is injected by the bus (sync + async) to keep this module
    /// free of bus/registry internals.
    ///
    /// The decision tree itself lives in exactly one place: <see cref="BuildPlan"/>
    /// computes the triage → strategy → Skip/Abort/Fallback/Retry outcome; the sync and
    /// async entry points only dispatch the failed signal and execute the fallback
    /// through their own (sync/async) channels. A policy change lands in one routine.
    /// </summary>
    [Preserve]
    public sealed class RecoveryEngine
    {
        private readonly NexusDI _container;
        private readonly Action<CommandFailedSignal> _fireFailedSync;
        private readonly Func<CommandFailedSignal, ValueTask> _fireFailedAsync;
        private CommandExecutor _executor;

        public RecoveryEngine(NexusDI container, Action<CommandFailedSignal> fireFailedSync, Func<CommandFailedSignal, ValueTask> fireFailedAsync)
        {
            _container = container;
            _fireFailedSync = fireFailedSync;
            _fireFailedAsync = fireFailedAsync;
        }

        /// <summary>Wired after construction (fallback execution needs the executor, which needs this engine).</summary>
        internal void AttachExecutor(CommandExecutor executor) => _executor = executor;

        // ─── Sync entry point ────────────────────────────────────────────────────
        public RecoveryAction HandleErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
        {
            var plan = BuildPlan(ex, commandType, signal, retryCount, asyncContext: false);
            return ExecuteSyncPlan(plan, signal);
        }

        public RecoveryAction HandleErrorWithDecision<TSignal>(Exception ex, Type commandType, TSignal signal, ref int retryCount)
        {
            var plan = BuildPlan(ex, commandType, signal, retryCount, asyncContext: false);
            return ExecuteSyncPlan(plan, signal);
        }

        // ─── Async entry point ───────────────────────────────────────────────────
        public async ValueTask<RecoveryAction> HandleErrorWithDecisionAsync(Exception ex, Type commandType, object signal, int retryCount, CancellationToken ct)
        {
            var plan = BuildPlan(ex, commandType, signal, retryCount, asyncContext: true);
            return await ExecuteAsyncPlan(plan, signal, ct);
        }

        // ─── The single decision tree ────────────────────────────────────────────
        /// <summary>
        /// Computes the recovery outcome for a command failure. This is the ONLY place
        /// the decision logic lives: exception triage, failed-signal construction, the
        /// CommandFailedSignal guard, strategy resolution, and the Skip/Abort/Fallback/
        /// Retry interpretation (including fallback-type validation and retry limits).
        /// The result is a plan the sync/async entry points execute through their own
        /// dispatch channels.
        /// </summary>
        private RecoveryPlan BuildPlan(Exception ex, Type commandType, object signal, int retryCount, bool asyncContext)
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
                return RecoveryPlan.AbortPlan(failedSignal);
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
                        return RecoveryPlan.SkipPlan(failedSignal);
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        // İ4-fix: throw the typed abort exception instead of a bare
                        // InvalidOperationException. The strategy-failure catch filter below
                        // matches on TYPE now, so it can no longer misfire when a strategy
                        // wraps the original exception as InnerException for its own reasons.
                        throw new NexusRecoveryAbortException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null &&
                            (asyncContext
                                ? IsValidFallbackType(decision.FallbackCommandType, signal)
                                : IsSyncCapableFallbackType(decision.FallbackCommandType, signal)))
                        {
                            bool isAsync = asyncContext &&
                                (typeof(IAsyncCommand).IsAssignableFrom(decision.FallbackCommandType)
                                 || SignalBus.ImplementsGenericInterface(decision.FallbackCommandType, typeof(IAsyncCommand<>)));
                            return RecoveryPlan.FallbackPlan(failedSignal, decision.FallbackCommandType, isAsync);
                        }

                        // Reject fallback types that cannot execute in this context — sync
                        // rejects async-only types (it cannot await them), async rejects
                        // types implementing no supported command interface — so we neither
                        // silently no-op nor recurse forever on the same decision.
                        if (decision.FallbackCommandType != null)
                        {
                            NexusRuntime.Logger?.LogError(
                                asyncContext
                                    ? $"[Nexus] Fallback command '{decision.FallbackCommandType.Name}' implements no supported command interface for signal '{signal?.GetType().Name ?? "unknown"}'. Treating as Skip."
                                    : $"[Nexus] Fallback command '{decision.FallbackCommandType.Name}' cannot execute synchronously for signal '{signal?.GetType().Name ?? "unknown"}'. Treating as Skip.");
                        }

                        // T6 fix: a rejected (or absent) fallback must still surface the
                        // failure. Returning a Fallback plan with a null type made
                        // ExecuteSyncPlan/ExecuteAsyncPlan take the "nominal Fallback" path
                        // WITHOUT dispatching the CommandFailedSignal — the error was logged
                        // and then silently dropped. Routing through the Skip plan guarantees
                        // the failed signal is dispatched (observable, recoverable) while the
                        // log message above explains why the fallback was not run.
                        return RecoveryPlan.SkipPlan(failedSignal);
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            NexusRuntime.Logger?.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            // İ4-fix: typed abort exception (see Abort path above) so the
                            // strategy-failure filter below does not swallow it as a strategy
                            // error via InnerException identity.
                            throw new NexusRecoveryAbortException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryPlan.RetryPlan(failedSignal);
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is NexusRecoveryAbortException))
                {
                    // T6 fix: strategy failures were only written to the console; the
                    // diagnostics layer (ErrorCollection) never saw them, so editor tooling
                    // and ErrorCollection subscribers could not react. Surface the strategy
                    // error through the same collection pipeline the original command error
                    // uses — never silently swallowed.
                    // PRESERVE ORIGINAL EXCEPTION: Collect both the strategy failure AND the original command exception
                    NexusRuntime.Logger?.LogError(
                        asyncContext
                            ? $"[Nexus] Error recovery strategy failed: {strategyEx.Message}\nOriginal command exception: {ex.Message}"
                            : $"[Nexus] Error recovery strategy failed: {strategyEx.Message}\nOriginal command exception: {ex.Message}\n{ex.StackTrace}");
                    ErrorCollection.CollectException(strategyEx, ErrorCollection.ErrorCategory.Command,
                        $"Recovery strategy '{strategyEx.TargetSite?.DeclaringType?.Name ?? strategyEx.GetType().Name}' failed while handling command {commandType.Name}");
                    // Also collect the original exception to ensure it's not lost
                    ErrorCollection.CollectException(ex, ErrorCollection.ErrorCategory.Command,
                        $"Original command failure that triggered recovery strategy: {commandType.Name}");
                }
            }

            return RecoveryPlan.SkipPlan(failedSignal);
        }

        private int _fallbackDepth = 0;
        private const int MaxFallbackDepth = 3;

        private RecoveryAction ExecuteSyncPlan(RecoveryPlan plan, object signal)
        {
            if (plan.Action == RecoveryAction.Skip)
            {
                _fireFailedSync(plan.FailedSignal);
                return RecoveryAction.Skip;
            }
            if (plan.Action == RecoveryAction.Abort)
            {
                return RecoveryAction.Abort;
            }
            if (plan.Action == RecoveryAction.Fallback)
            {
                if (_fallbackDepth >= MaxFallbackDepth)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Max fallback depth ({MaxFallbackDepth}) exceeded. Aborting.");
                    return RecoveryAction.Abort;
                }
                if (plan.FallbackType != null)
                {
                    if (!IsSyncCapableFallbackType(plan.FallbackType, signal))
                    {
                        _fireFailedSync(plan.FailedSignal);
                        return RecoveryAction.Skip;
                    }

                    _fallbackDepth++;
                    try
                    {
                        // Use negative priority to ensure fallbacks run after normal handlers
                        _executor.Execute(new CommandHandlerInfo(plan.FallbackType, ExecutionMode.Sequential, -1, false), signal);
                    }
                    finally
                    {
                        _fallbackDepth--;
                    }
                }
                return RecoveryAction.Fallback;
            }
            return RecoveryAction.Retry;
        }

        private async ValueTask<RecoveryAction> ExecuteAsyncPlan(RecoveryPlan plan, object signal, CancellationToken ct)
        {
            if (plan.Action == RecoveryAction.Skip)
            {
                // P0-4 fix: async-safe dispatch — awaits the full handler chain
                // and captures errors instead of throwing a sync/async mismatch.
                await _fireFailedAsync(plan.FailedSignal);
                return RecoveryAction.Skip;
            }
            if (plan.Action == RecoveryAction.Abort)
            {
                return RecoveryAction.Abort;
            }
            if (plan.Action == RecoveryAction.Fallback)
            {
                if (_fallbackDepth >= MaxFallbackDepth)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Max fallback depth ({MaxFallbackDepth}) exceeded. Aborting.");
                    return RecoveryAction.Abort;
                }
                if (plan.FallbackType != null)
                {
                    _fallbackDepth++;
                    try
                    {
                        if (plan.FallbackIsAsync)
                        {
                            // E-4/P0-1-aligned: recognize generic-only async fallback commands too.
                            await _executor.ExecuteAsync(new CommandHandlerInfo(plan.FallbackType, ExecutionMode.Sequential, -1, true), signal, ct);
                        }
                        else
                        {
                            await _executor.ExecuteAsync(new CommandHandlerInfo(plan.FallbackType, ExecutionMode.Sequential, -1, false), signal, ct);
                        }
                    }
                    finally
                    {
                        _fallbackDepth--;
                    }
                }
                else
                {
                    // T6 fix (defensive): BuildPlan now routes rejected fallbacks through
                    // SkipPlan, so a null fallback type here means a plan constructed by a
                    // path that bypassed BuildPlan. Fire the failed signal rather than
                    // returning a nominal Fallback that would silently drop the error.
                    await _fireFailedAsync(plan.FailedSignal);
                    return RecoveryAction.Skip;
                }
                return RecoveryAction.Fallback;
            }
            return RecoveryAction.Retry;
        }

        // ─── Plan ────────────────────────────────────────────────────────────────
        private readonly struct RecoveryPlan
        {
            public readonly RecoveryAction Action;
            public readonly CommandFailedSignal FailedSignal;
            public readonly Type FallbackType;
            public readonly bool FallbackIsAsync;

            private RecoveryPlan(RecoveryAction action, CommandFailedSignal failedSignal, Type fallbackType, bool fallbackIsAsync)
            {
                Action = action;
                FailedSignal = failedSignal;
                FallbackType = fallbackType;
                FallbackIsAsync = fallbackIsAsync;
            }

            public static RecoveryPlan SkipPlan(CommandFailedSignal failedSignal)
                => new(RecoveryAction.Skip, failedSignal, null, false);

            public static RecoveryPlan AbortPlan(CommandFailedSignal failedSignal)
                => new(RecoveryAction.Abort, failedSignal, null, false);

            public static RecoveryPlan RetryPlan(CommandFailedSignal failedSignal)
                => new(RecoveryAction.Retry, failedSignal, null, false);

            public static RecoveryPlan FallbackPlan(CommandFailedSignal failedSignal, Type fallbackType, bool fallbackIsAsync)
                => new(RecoveryAction.Fallback, failedSignal, fallbackType, fallbackIsAsync);
        }

        /// <summary>
        /// True if <paramref name="fallbackType"/> implements a command interface usable by the
        /// object-based async dispatch paths for <paramref name="signal"/>: non-generic
        /// ICommand/IAsyncCommand, or the generic ICommand&lt;TSignal&gt;/IAsyncCommand&lt;TSignal&gt;
        /// matching the signal type.
        /// </summary>
        internal static bool IsValidFallbackType(Type fallbackType, object signal)
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
        internal static bool IsSyncCapableFallbackType(Type fallbackType, object signal)
        {
            if (typeof(ICommand).IsAssignableFrom(fallbackType)) return true;
            if (signal == null) return false;
            return typeof(ICommand<>).MakeGenericType(signal.GetType()).IsAssignableFrom(fallbackType);
        }
    }
}
