using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Service contract for spawning 0-GC pooled floating UI numbers (+$500, +$1.2M, -25 HP) in World-Space.
    /// </summary>
    [Preserve]
    public interface IFloatingTextService
    {
        void SpawnFloatingText(string text, Vector3 worldPosition, Color color, float duration = 1.0f, float riseHeight = 1.5f);
    }
}
