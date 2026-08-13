using Unity.Profiling.Editor;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Ready-made "Nexus" chart in the Profiler window.
    ///
    /// Per the Unity 6 custom-module API ([ProfilerModuleMetadata] + a no-arg constructor
    /// passing <see cref="ProfilerCounterDescriptor"/>s to the base), the Profiler window
    /// discovers this class automatically. Open Window → Analysis → Profiler and select
    /// "Nexus" from the module dropdown to chart signal dispatch, command execution,
    /// composite-trigger processing, and DI resolve throughput.
    ///
    /// Counters only appear once frames with data have been captured (Profiler Module
    /// Editor note: record data first). In non-development builds the counters are
    /// compiled out, so profile a Development build.
    /// </summary>
    [ProfilerModuleMetadata("Nexus")]
    public sealed class NexusProfilerModule : ProfilerModule
    {
        private static readonly ProfilerCounterDescriptor[] k_Counters =
        {
            new ProfilerCounterDescriptor(NexusProfiler.SignalsDispatchedName, NexusProfiler.Category),
            new ProfilerCounterDescriptor(NexusProfiler.CommandsExecutedName, NexusProfiler.Category),
            new ProfilerCounterDescriptor(NexusProfiler.CompositeTriggersProcessedName, NexusProfiler.Category),
            new ProfilerCounterDescriptor(NexusProfiler.ResolvesPerformedName, NexusProfiler.Category),
        };

        public NexusProfilerModule() : base(k_Counters) { }
    }
}
