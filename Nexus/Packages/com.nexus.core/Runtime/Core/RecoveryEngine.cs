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

        public RecoveryAction HandleErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
            => HandleErrorWithDecision<object>(ex, commandType, signal, ref retryCount);

        public RecoveryAction HandleErrorWithDecision<TSignal>(Exception ex, Type commandType, TSignal signal, ref int retryCount)
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
                        _fireFailedSync(failedSignal);
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
                            _executor.Execute(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
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

            _fireFailedSync(failedSignal);
            return RecoveryAction.Skip;
        }

        public async ValueTask<RecoveryAction> HandleErrorWithDecisionAsync(Exception ex, Type commandType, object signal, int retryCount, CancellationToken ct)
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
                        await _fireFailedAsync(failedSignal);
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
                                || SignalBus.ImplementsGenericInterface(decision.FallbackCommandType, typeof(IAsyncCommand<>));
                            if (isAsync)
                            {
                                await _executor.ExecuteAsync(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, true), signal, ct);
                            }
                            else
                            {
                                _executor.Execute(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
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
            await _fireFailedAsync(failedSignal);
            return RecoveryAction.Skip;
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
