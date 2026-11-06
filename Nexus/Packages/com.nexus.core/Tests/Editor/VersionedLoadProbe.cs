using Nexus.Core;

namespace Nexus.Editor.Tests
{
    // An asset type for VersionedScriptableObjectLoadTests: Unity saves a ScriptableObject asset only for a class whose
    // script file carries its name.
    public class VersionedLoadProbe : VersionedScriptableObject
    {
        [System.NonSerialized] public int Migrations;
        [System.NonSerialized] public int MigratedFrom = -1;

        public override int CurrentVersion => 2;

        public void SetStoredVersion(int version) => Version = version;

        protected override void Migrate(int fromVersion)
        {
            Migrations++;
            MigratedFrom = fromVersion;
        }
    }
}
