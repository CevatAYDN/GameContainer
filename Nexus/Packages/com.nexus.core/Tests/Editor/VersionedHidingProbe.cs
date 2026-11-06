using UnityEngine;

namespace Nexus.Editor.Tests
{
    // Implements ISerializationCallbackReceiver itself, which hides VersionedScriptableObject's hook (#297).
    public class VersionedHidingProbe : VersionedLoadProbe, ISerializationCallbackReceiver
    {
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() { }
    }
}
