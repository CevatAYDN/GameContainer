using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// ScriptableObject manifest that controls project-wide Nexus scaffolding settings.
    /// Defines default context names, sample generation flags, and editor tool preferences.
    /// Created via Assets/Create/Nexus/Bootstrap Manifest.
    /// </summary>
    [CreateAssetMenu(fileName = "NexusBootstrapManifest", menuName = "Nexus/Bootstrap Manifest")]
    public class NexusBootstrapManifest : VersionedScriptableObject
    {
        /// <summary>Current version of this manifest layout.</summary>
        public override int CurrentVersion => 1;

        [Header("Project Scaffold")]
        public string[] DefaultContextNames = new string[] { "Global", "Gameplay", "UI" };
        public bool GenerateSampleSignals = true;
        public bool GenerateSampleCommands = true;

        [Header("Editor Tools")]
        public bool EnableInspector = true;

        protected override void Migrate(int fromVersion)
        {
            // First version, no migration needed yet.
        }
    }
}
