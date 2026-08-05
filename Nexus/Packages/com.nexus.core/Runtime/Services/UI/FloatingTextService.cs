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

            lock (_lock)
            {
                _activeTexts.Add(new ActiveFloatingText
                {
                    Text = text,
                    StartPosition = worldPosition,
                    Color = color,
                    Duration = Math.Max(0.1f, duration),
                    ElapsedTime = 0f,
                    RiseHeight = riseHeight
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
