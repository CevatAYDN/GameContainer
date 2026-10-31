using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// What the <see cref="UICanvasSystem"/>s sharing one [Nexus_UICanvas] know of each other (issue #185): which of them
    /// use it, which have a Modal-or-higher window open, and whether a UICanvasSystem created it. It lives on the canvas
    /// and goes with it.
    /// </summary>
    [Preserve]
    [AddComponentMenu("")]
    internal sealed class UICanvasUsers : MonoBehaviour
    {
        internal bool CreatedByNexus;
        internal readonly HashSet<UICanvasSystem> Users = new();
        internal readonly HashSet<UICanvasSystem> ModalOwners = new();
    }
}
