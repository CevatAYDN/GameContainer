using UnityEngine;

namespace Nexus.Capture
{
    /// <summary>
    /// Capture Mode deactivates this GameObject while the UI is hidden and reactivates it afterwards.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Nexus/Capture/Hide")]
    public sealed class CaptureHide : MonoBehaviour
    {
    }
}
