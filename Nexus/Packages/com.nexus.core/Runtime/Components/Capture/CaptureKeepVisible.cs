using UnityEngine;

namespace Nexus.Capture
{
    /// <summary>
    /// A Canvas or UIDocument on this GameObject stays visible when Capture Mode hides the UI.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Nexus/Capture/Keep Visible")]
    public sealed class CaptureKeepVisible : MonoBehaviour
    {
    }
}
