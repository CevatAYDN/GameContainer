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
        [Tooltip("Assemblies to scan for SignalHandlers. If empty, uses the calling assembly.")]
        public string[] AssemblyScopes;

        [Tooltip("Name of contexts this context depends on.")]
        public string[] DependsOn;

        [Header("Feature Flags")]
        public bool EnableAnalytics;
        public bool EnableDebugSignals;

        [Header("Performance")]
        public int CommandPoolInitialSize = 4;

        [Header("Metadata")]
        public string ScopeTag;

        protected override void Migrate(int fromVersion)
        {
            // v1 is the initial version, no migration needed yet.
        }
    }
}
