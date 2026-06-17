using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    public enum RecoveryAction
    {
        Skip,
        Retry,
        Abort,
        Fallback
    }

    [Preserve]
    public readonly struct RecoveryDecision
    {
        public readonly RecoveryAction Action;
        public readonly Type FallbackCommandType;
        public readonly int MaxRetries;

        public RecoveryDecision(RecoveryAction action, Type fallbackCommandType, int maxRetries)
        {
            Action = action;
            FallbackCommandType = fallbackCommandType;
            MaxRetries = maxRetries;
        }

        public static RecoveryDecision Skip()     
            => new(RecoveryAction.Skip, null, 0);
            
        public static RecoveryDecision Retry(int max = 3) 
            => new(RecoveryAction.Retry, null, max);
            
        public static RecoveryDecision Abort()    
            => new(RecoveryAction.Abort, null, 0);
            
        public static RecoveryDecision Fallback<T>() where T : ICommand 
            => new(RecoveryAction.Fallback, typeof(T), 0);
    }

    [Preserve]
    public readonly struct CommandFailureContext
    {
        public readonly Exception Exception;
        public readonly Type CommandType;
        public readonly object Signal;
        public readonly int RetryCount;

        public CommandFailureContext(Exception exception, Type commandType, object signal, int retryCount)
        {
            Exception = exception;
            CommandType = commandType;
            Signal = signal;
            RetryCount = retryCount;
        }
    }
}
