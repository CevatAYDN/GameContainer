using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Pipelines
{
    /// <summary>
    /// Encapsulates command pool management, execution mode handling (Sequential, Concurrent, Exclusive), and command invocation.
    /// Follows Single Responsibility Principle (SRP) to isolate command execution from dispatch routing.
    /// </summary>
    public sealed class CommandExecutionPipeline
    {
        private readonly CommandPoolManager _poolManager;

        public CommandExecutionPipeline(CommandPoolManager poolManager)
        {
            _poolManager = poolManager;
        }

        public CommandPoolManager PoolManager => _poolManager;

        /// <summary>
        /// Executes a synchronous command with optional command pooling.
        /// </summary>
        public void ExecuteCommand<TCommand, TSignal>(TCommand command, TSignal signal)
            where TCommand : class
            where TSignal : struct
        {
            if (command is ICommand<TSignal> executable)
            {
                try
                {
                    executable.Execute(signal);
                }
                finally
                {
                    if (_poolManager != null)
                    {
                        _poolManager.ReturnCommand(typeof(TCommand), command);
                    }
                }
            }
        }

        /// <summary>
        /// Executes an asynchronous command task with safety wrappers.
        /// </summary>
        public async ValueTask ExecuteCommandAsync<TCommand, TSignal>(TCommand command, TSignal signal, CancellationToken ct = default)
            where TCommand : class
            where TSignal : struct
        {
            if (command is IAsyncCommand<TSignal> asyncExecutable)
            {
                try
                {
                    await asyncExecutable.ExecuteAsync(signal, ct);
                }
                finally
                {
                    if (_poolManager != null)
                    {
                        _poolManager.ReturnCommand(typeof(TCommand), command);
                    }
                }
            }
        }
    }
}
