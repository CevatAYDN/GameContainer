using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>Defines the recovery action to take when a command fails.</summary>
    public enum RecoveryAction
    {
        /// <summary>Skip the failed command and continue.</summary>
        Skip,
        /// <summary>Retry the command up to the specified maximum retry count.</summary>
        Retry,
        /// <summary>Abort the entire signal chain.</summary>
        Abort,
        /// <summary>Execute a fallback command instead.</summary>
        Fallback
    }

    /// <summary>
    /// Represents a recovery decision made when a command fails.
    /// Created via static factory methods: <see cref="Skip"/>, <see cref="Retry"/>, <see cref="Abort"/>, <see cref="Fallback{T}"/>.
    /// </summary>
    [Preserve]
    public readonly struct RecoveryDecision
    {
        /// <summary>The recovery action to take.</summary>
        public readonly RecoveryAction Action;
        /// <summary>The fallback command type (only valid when Action is Fallback).</summary>
        public readonly Type FallbackCommandType;
        /// <summary>Maximum retry attempts (only valid when Action is Retry).</summary>
        public readonly int MaxRetries;

        /// <summary>Creates a new <see cref="RecoveryDecision"/>.</summary>
        /// <param name="action">The recovery action.</param>
        /// <param name="fallbackCommandType">Fallback command type (for Fallback action).</param>
        /// <param name="maxRetries">Maximum retry count (for Retry action).</param>
        public RecoveryDecision(RecoveryAction action, Type fallbackCommandType, int maxRetries)
        {
            Action = action;
            FallbackCommandType = fallbackCommandType;
            MaxRetries = maxRetries;
        }

        /// <summary>Skips the failed command and continues execution.</summary>
        public static RecoveryDecision Skip()     
            => new(RecoveryAction.Skip, null, 0);

        /// <summary>Retries the command up to the specified number of times.</summary>
        /// <param name="max">Maximum retry attempts (default: 3).</param>
        public static RecoveryDecision Retry(int max = 3) 
            => new(RecoveryAction.Retry, null, max);

        /// <summary>Aborts the entire signal execution chain.</summary>
        public static RecoveryDecision Abort()    
            => new(RecoveryAction.Abort, null, 0);

        /// <summary>Executes a fallback synchronous command instead.</summary>
        /// <typeparam name="T">The fallback command type (must implement <see cref="ICommand"/>).</typeparam>
        public static RecoveryDecision Fallback<T>() where T : ICommand 
            => new(RecoveryAction.Fallback, typeof(T), 0);

        /// <summary>Executes a fallback asynchronous command instead.</summary>
        /// <typeparam name="T">The fallback command type (must implement <see cref="IAsyncCommand"/>).</typeparam>
        public static RecoveryDecision FallbackAsync<T>() where T : IAsyncCommand 
            => new(RecoveryAction.Fallback, typeof(T), 0);
    }

    /// <summary>
    /// Provides context about a command failure, including the exception, command type, signal data, and retry count.
    /// </summary>
    [Preserve]
    public readonly struct CommandFailureContext
    {
        /// <summary>The exception that caused the failure.</summary>
        public readonly Exception Exception;
        /// <summary>The type of command that failed.</summary>
        public readonly Type CommandType;
        /// <summary>The signal that triggered the failed command.</summary>
        public readonly object Signal;
        /// <summary>The number of retry attempts made (0 for first failure).</summary>
        public readonly int RetryCount;
        /// <summary>True if the failure was caused by a timeout (OperationCanceledException from timeout).</summary>
        public readonly bool IsTimeout;

        /// <summary>Creates a new <see cref="CommandFailureContext"/>.</summary>
        public CommandFailureContext(Exception exception, Type commandType, object signal, int retryCount) : this(exception, commandType, signal, retryCount, false) { }

        /// <summary>Creates a new <see cref="CommandFailureContext"/> with timeout info.</summary>
        public CommandFailureContext(Exception exception, Type commandType, object signal, int retryCount, bool isTimeout)
        {
            Exception = exception;
            CommandType = commandType;
            Signal = signal;
            RetryCount = retryCount;
            IsTimeout = isTimeout;
        }
    }
}
