namespace Nexus.Core
{
    /// <summary>
    /// Abstract base class for model configuration and initial data stored as <see cref="ScriptableObject"/>.
    /// Developers can derive from this class to define persistent model data assets.
    /// </summary>
    public abstract class ModelData : VersionedScriptableObject
    {
        // Base ScriptableObject class for holding model configuration and initial data.
        // Geliştiriciler bu sınıftan türeterek kendi model verilerini tanımlayabilirler.
    }
}
