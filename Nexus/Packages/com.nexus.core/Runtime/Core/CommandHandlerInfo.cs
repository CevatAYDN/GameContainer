using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Metadata describing a registered command handler, including its type, execution mode, priority, and sync/async nature.
    /// </summary>
    [Preserve]
    public class CommandHandlerInfo
    {
        /// <summary>The <see cref="Type"/> of the command class.</summary>
        public Type CommandType { get; }

        /// <summary>The execution mode (Sequential, Concurrent, Exclusive, or CompositeTrigger).</summary>
        public ExecutionMode Mode { get; }

        /// <summary>Execution priority; lower values run first.</summary>
        public int Priority { get; }

        /// <summary>True if the command implements <see cref="IAsyncCommand"/>.</summary>
        public bool IsAsync { get; }

        /// <summary>Creates a new <see cref="CommandHandlerInfo"/> instance.</summary>
        /// <param name="commandType">The <see cref="Type"/> of the command.</param>
        /// <param name="mode">The execution mode for this handler.</param>
        /// <param name="priority">Execution priority (lower runs first).</param>
        /// <param name="isAsync">Whether the command is asynchronous (<see cref="IAsyncCommand"/>).</param>
        public CommandHandlerInfo(Type commandType, ExecutionMode mode, int priority, bool isAsync)
        {
            CommandType = commandType;
            Mode = mode;
            Priority = priority;
            IsAsync = isAsync;
        }
    }
}
