using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    [Preserve]
    public class FloatingTextService : NexusService<IFloatingTextService>, IFloatingTextService
    {
        public struct ActiveFloatingText
        {
            public string Text;
            public Vector3 StartPosition;
            public Color Color;
            public float Duration;
            public float ElapsedTime;
            public float RiseHeight;
            // Wall-clock spawn stamp (Time.unscaledTime) — drives the self-contained
            // expiry sweep in SpawnFloatingText (R1): no external per-frame driver
            // pumps this service, so elapsed entries would otherwise accumulate forever.
            public float SpawnTime;
        }

        private readonly List<ActiveFloatingText> _activeTexts = new();
        private readonly object _lock = new();

        // Snapshot copy: exposing the live list would let callers observe it while
        // UpdateService mutates it under _lock (torn enumeration / InvalidOperationException).
        public IReadOnlyList<ActiveFloatingText> ActiveTexts
        {
            get { lock (_lock) return _activeTexts.ToArray(); }
        }

        public void SpawnFloatingText(string text, Vector3 worldPosition, Color color, float duration = 1.0f, float riseHeight = 1.5f)
        {
            if (string.IsNullOrEmpty(text)) return;

            float now = Time.unscaledTime;
            lock (_lock)
            {
                // Wall-clock expiry sweep: UpdateService is not wired to any per-frame
                // driver in the default runtime, so without this the expired entries
                // would never be removed (unbounded growth + per-access ToArray cost).
                // Sweeping on spawn keeps cleanup self-contained; UpdateService (if a
                // consumer wires it) remains the exact delta-time pump for animation.
                for (int i = _activeTexts.Count - 1; i >= 0; i--)
                {
                    if (now - _activeTexts[i].SpawnTime >= _activeTexts[i].Duration)
                    {
                        _activeTexts.RemoveAt(i);
                    }
                }

                _activeTexts.Add(new ActiveFloatingText
                {
                    Text = text,
                    StartPosition = worldPosition,
                    Color = color,
                    Duration = Math.Max(0.1f, duration),
                    ElapsedTime = 0f,
                    RiseHeight = riseHeight,
                    SpawnTime = now
                });
            }
        }

        public void UpdateService(float deltaTime)
        {
            lock (_lock)
            {
                for (int i = _activeTexts.Count - 1; i >= 0; i--)
                {
                    var item = _activeTexts[i];
                    item.ElapsedTime += deltaTime;

                    if (item.ElapsedTime >= item.Duration)
                    {
                        _activeTexts.RemoveAt(i);
                    }
                    else
                    {
                        _activeTexts[i] = item;
                    }
                }
            }
        }
    }
}
