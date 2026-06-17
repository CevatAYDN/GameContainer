using UnityEngine;

namespace Nexus.Core
{
    [CreateAssetMenu(fileName = "NexusBootstrapManifest", menuName = "Nexus/Bootstrap Manifest")]
    public class NexusBootstrapManifest : VersionedScriptableObject
    {
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
