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

        /// <summary>Execution priority; higher values run first.</summary>
        public int Priority { get; }

        /// <summary>Bitmask of signals received so far. Only set by SignalBus internals.</summary>
        public ulong CurrentMask { get; internal set; }

        /// <summary>Bitmask of all required signals (all bits set).</summary>
        public ulong TargetMask { get; }

        /// <summary>True once all required signals have been received. Only set by SignalBus internals.</summary>
        public bool IsCompleted { get; internal set; }

        /// <summary>
        /// Most recent boxed payload captured per required signal (indexed identically to
        /// <see cref="RequiredSignals"/>). Populated lazily only when a matching signal is fired,
        /// so signals without composite triggers never allocate.
        /// </summary>
        private readonly object[] _capturedPayloads;

        /// <summary>Stores the most recent payload for the required signal at the given index.</summary>
        public void CapturePayload(int index, object payload) => _capturedPayloads[index] = payload;

        /// <summary>Builds an immutable snapshot of currently captured payloads for command dispatch.</summary>
        public object[] SnapshotPayloads()
        {
            var copy = new object[_capturedPayloads.Length];
            Array.Copy(_capturedPayloads, copy, _capturedPayloads.Length);
            return copy;
        }

        /// <summary>Clears captured payloads (called on reset for repeatable triggers).</summary>
        public void ClearPayloads() => Array.Clear(_capturedPayloads, 0, _capturedPayloads.Length);

        /// <summary>Creates a new <see cref="CompositeTriggerState"/> instance.</summary>
        /// <param name="commandType">The composite trigger command type.</param>
        /// <param name="requiredSignals">The signal types required for completion.</param>
        /// <param name="oneShot">If true, fires once and is removed.</param>
        /// <param name="priority">Execution priority (higher runs first).</param>
        public CompositeTriggerState(Type commandType, Type[] requiredSignals, bool oneShot, int priority)
        {
            CommandType = commandType;
            RequiredSignals = requiredSignals;
            OneShot = oneShot;
            Priority = priority;
            // Safe bitmask calculation: for count == 64, (1UL << 64) is undefined
            // (the runtime masks shift to low-order 6 bits, yielding 1 instead of 0).
            // We handle 64 as a special case: set all 64 bits explicitly.
            TargetMask = requiredSignals.Length == 64
                ? ulong.MaxValue
                : (1UL << requiredSignals.Length) - 1;
            CurrentMask = 0;
            IsCompleted = false;
            _capturedPayloads = new object[requiredSignals.Length];
        }
    }
}
