using System;

namespace Nexus.Core
{
    /// <summary>
    /// Tracks the accumulated state of a composite trigger command.
    /// A composite trigger fires only after all required signals have been received.
    /// </summary>
    public class CompositeTriggerState
    {
        /// <summary>The <see cref="Type"/> of the composite trigger command.</summary>
        public Type CommandType { get; }

        /// <summary>The signal types that must all be received before the composite trigger fires.</summary>
        public Type[] RequiredSignals { get; }

        /// <summary>If true, the trigger fires only once and is then removed.</summary>
        public bool OneShot { get; }

        /// <summary>Execution priority; lower values run first.</summary>
        public int Priority { get; }

        /// <summary>Bitmask of signals received so far.</summary>
        public ulong CurrentMask { get; set; }

        /// <summary>Bitmask of all required signals (all bits set).</summary>
        public ulong TargetMask { get; }

        /// <summary>True once all required signals have been received.</summary>
        public bool IsCompleted { get; set; }

        /// <summary>Creates a new <see cref="CompositeTriggerState"/> instance.</summary>
        /// <param name="commandType">The composite trigger command type.</param>
        /// <param name="requiredSignals">The signal types required for completion.</param>
        /// <param name="oneShot">If true, fires once and is removed.</param>
        /// <param name="priority">Execution priority (lower runs first).</param>
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
