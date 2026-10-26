using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Names the context a screen belongs to. <see cref="UIManager"/> puts it on every screen it instantiates under the
    /// shared [Nexus_UICanvas], which has no <see cref="Root"/>, so the screen's views register with the context of the
    /// UIManager that opened it even when the scene holds several Roots (issue #67).
    /// </summary>
    [Preserve]
    [AddComponentMenu("")]
    internal sealed class ScreenOwner : MonoBehaviour
    {
        internal IContext Context;
    }
}
