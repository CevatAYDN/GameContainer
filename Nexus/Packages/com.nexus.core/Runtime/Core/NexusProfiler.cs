using Unity.Profiling;

namespace Nexus.Core
{
    /// <summary>
    /// Central profiler instrumentation for Nexus hot paths.
    ///
    /// Two layers, both non-allocating:
    /// - <see cref="ProfilerMarker"/> samples (per-method, e.g. <c>Nexus.Signal.Dispatch</c>)
    ///   already exist on the SignalBus/CommandExecutor/TickService paths; this class adds the
    ///   counter layer on top so the framework's own throughput is visible as CHARTABLE data.
    /// - <see cref="ProfilerCounterValue{T}"/> counters: written on every dispatch/resolve and
    ///   flushed at end of frame. Their bodies are compiled only where <c>ENABLE_PROFILER</c> is defined
    ///   (com.unity.profiling.core): the editor, and a player of Unity 6.6's Instrumented, Checked or
    ///   Debug managed code variant. There the hot-path cost is a single store; a Release variant player
    ///   pays nothing (issue #194).
    ///
    /// Editor: the <c>NexusProfilerModule</c> (com.nexus.core.editor, namespace Nexus.Editor)
    /// surfaces these counters as a ready-made "Nexus" chart in the Profiler window.
    /// </summary>
    public static class NexusProfiler
    {
        /// <summary>
        /// Category shared by all Nexus counters. Kept as <see cref="ProfilerCategory.Scripts"/>
        /// (the stable built-in category) so counters are always discoverable in the Profiler
        /// Module Editor without a custom category registration.
        /// </summary>
        public static readonly ProfilerCategory Category = ProfilerCategory.Scripts;

        // Names are public constants: the editor ProfilerModule and the ProfilerRecorder /
        // FrameDataView consumers reference counters by name.
        public const string SignalsDispatchedName = "Nexus/Signals Dispatched";
        public const string CommandsExecutedName = "Nexus/Commands Executed";
        public const string CompositeTriggersProcessedName = "Nexus/Composite Triggers Processed";
        public const string ResolvesPerformedName = "Nexus/DI Resolves";

        /// <summary>Total signal dispatches (sync + async) entering the bus. Incremented per FireInternal entry.</summary>
        public static readonly ProfilerCounterValue<int> SignalsDispatched =
            new(Category, SignalsDispatchedName, ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        /// <summary>Command executions entering CommandExecutor (all four execution paths), once per execution; a recovery retry is not counted again.</summary>
        public static readonly ProfilerCounterValue<int> CommandsExecuted =
            new(Category, CommandsExecutedName, ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        /// <summary>Composite-trigger passes, sync and async, for a fired signal type that has at least one composite trigger registered.</summary>
        public static readonly ProfilerCounterValue<int> CompositeTriggersProcessed =
            new(Category, CompositeTriggersProcessedName, ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        /// <summary>DI resolutions entering NexusDI.Resolve (root + named paths go through it).</summary>
        public static readonly ProfilerCounterValue<int> ResolvesPerformed =
            new(Category, ResolvesPerformedName, ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);
    }
}
