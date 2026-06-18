using System;

namespace Nexus.Core
{
    public class CompositeTriggerState
    {
        public Type CommandType { get; }
        public Type[] RequiredSignals { get; }
        public bool OneShot { get; }
        public int Priority { get; }
        public ulong CurrentMask { get; set; }
        public ulong TargetMask { get; }
        public bool IsCompleted { get; set; }

        public CompositeTriggerState(Type commandType, Type[] requiredSignals, bool oneShot, int priority)
        {
            CommandType = commandType;
            RequiredSignals = requiredSignals;
            OneShot = oneShot;
            Priority = priority;
            TargetMask = (1UL << requiredSignals.Length) - 1;
            CurrentMask = 0;
            IsCompleted = false;
        }
    }
}
