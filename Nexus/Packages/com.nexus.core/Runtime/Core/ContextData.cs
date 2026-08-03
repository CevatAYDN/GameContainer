using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// ScriptableObject configuration data for a Nexus context.
    /// Defines assembly scopes, dependencies, feature flags, performance settings, and metadata.
    /// Created via Assets/Create/Nexus/Context Data.
    /// </summary>
    [CreateAssetMenu(fileName = "ContextData", menuName = "Nexus/Context Data")]
    public class ContextData : VersionedScriptableObject
    {
        /// <summary>Current version of this data layout.</summary>
        public override int CurrentVersion => 1;

        [Header("Orchestration")]
        [Tooltip("Assemblies to scan for SignalHandlers. If empty, auto-scan is disabled unless enabled in code.")]
        public string[] AssemblyScopes;

        [Tooltip("Enable convention-based auto-discovery when no lifecycle is explicitly provided.")]
        public bool EnableAutoDiscovery = true;

        [Tooltip("Name of contexts this context depends on.")]
        public string[] DependsOn;

        [Header("Feature Flags")]
        public bool EnableAnalytics;
        public bool EnableDebugSignals;

        [Tooltip("When enabled, Inject() throws InvalidOperationException on unresolved [Inject] dependencies instead of logging and leaving null. Use [OptionalInject] to exempt specific members.")]
        public bool EnableStrictInjection;

        [Tooltip("When enabled, DI validation errors (missing bindings, captive dependencies, constructor explosion) throw NexusDiValidationException at startup instead of only logging. Recommended for development builds.")]
        public bool FailOnValidationErrors;

        [Header("Performance")]
        public int CommandPoolInitialSize = 4;
        public int CommandPoolMaxSize = 64;
        public int TracerRingBufferSize = 2000;

        [Header("Metadata")]
        public string ScopeTag;

        protected override void Migrate(int fromVersion)
        {
            // v1 is the initial version, no migration needed yet.
        }
    }
}
