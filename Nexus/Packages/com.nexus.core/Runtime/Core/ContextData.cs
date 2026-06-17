using UnityEngine;

namespace Nexus.Core
{
    [CreateAssetMenu(fileName = "ContextData", menuName = "Nexus/Context Data")]
    public class ContextData : ScriptableObject
    {
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
    }
}
