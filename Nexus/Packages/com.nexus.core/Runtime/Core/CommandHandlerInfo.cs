using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class CommandHandlerInfo
    {
        public Type CommandType { get; }
        public ExecutionMode Mode { get; }
        public int Priority { get; }
        public bool IsAsync { get; }

        public CommandHandlerInfo(Type commandType, ExecutionMode mode, int priority, bool isAsync)
        {
            CommandType = commandType;
            Mode = mode;
            Priority = priority;
            IsAsync = isAsync;
        }
    }
}
