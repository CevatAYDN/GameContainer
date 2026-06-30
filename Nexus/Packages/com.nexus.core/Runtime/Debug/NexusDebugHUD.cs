using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [AddComponentMenu("Nexus/Debug HUD")]
    [Preserve]
    public class NexusDebugHUD : MonoBehaviour
    {
        [Header("Display")]
        [SerializeField] private KeyCode toggleKey = KeyCode.F12;
        [SerializeField] private bool showOnStart = false;
        [SerializeField] private int maxLogLines = 50;

        [Header("Style")]
        [SerializeField] private int fontSize = 11;
        [SerializeField] private Color backgroundColor = new(0f, 0f, 0f, 0.75f);
        [SerializeField] private Color textColor = Color.white;
        [SerializeField] private Color signalColor = new(0.3f, 0.8f, 1f);
        [SerializeField] private Color errorColor = new(1f, 0.3f, 0.3f);
        [SerializeField] private Color warningColor = new(1f, 0.85f, 0.3f);

        private bool _isVisible;
        private readonly List<string> _logLines = new();
        private int _frameCount;
        private float _fps;
        private float _fpsTimer;

        // Cached styles — created once, reused every frame (no GC)
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private Texture2D _bgTexture;
        private float _lastBoxWidth;

        private void Start()
        {
            _isVisible = showOnStart;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                _isVisible = !_isVisible;
            }

            _frameCount++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _frameCount / _fpsTimer;
                _frameCount = 0;
                _fpsTimer = 0;
            }
        }

        private void EnsureStyles(float width)
        {
            bool sizeChanged = Math.Abs(width - _lastBoxWidth) > 1f;
            if (_boxStyle != null && !sizeChanged) return;

            _lastBoxWidth = width;

            if (_bgTexture != null)
            {
                Destroy(_bgTexture);
                _bgTexture = null;
            }
            _bgTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            _bgTexture.SetPixel(0, 0, backgroundColor);
            _bgTexture.SetPixel(0, 1, backgroundColor);
            _bgTexture.SetPixel(1, 0, backgroundColor);
            _bgTexture.SetPixel(1, 1, backgroundColor);
            _bgTexture.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = fontSize,
                normal = { textColor = textColor, background = _bgTexture },
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = true
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                richText = true,
                normal = { textColor = textColor }
            };
        }

        private void OnGUI()
        {
            if (!_isVisible) return;

            const float width = 420;
            const float x = 10;
            const float y = 10;

            EnsureStyles(width);

            const float headerHeight = 24;
            int ctxCount = NexusRuntime.ActiveContexts?.Count ?? 0;
            string header = $"<b>NEXUS DEBUG</b>  FPS: {_fps:F1}  |  {ctxCount} ctx(s)";
            GUI.Box(new Rect(x, y, width, headerHeight), header, _boxStyle);

            const float contentHeight = 300;
            float contentY = y + headerHeight + 2;

            GUILayout.BeginArea(new Rect(x, contentY, width, contentHeight), _boxStyle);
            GUILayout.BeginVertical();

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts != null && contexts.Count > 0)
            {
                GUILayout.Label($"<color=#66ff66>\u25a0 Contexts:</color> {contexts.Count} active", _labelStyle);
                foreach (var ctx in contexts)
                {
                    string parentInfo = ctx.Parent != null ? $" \u2192 parent: {ctx.Parent.ScopeTag}" : " (root)";
                    GUILayout.Label($"  {ctx.ScopeTag ?? "(no tag)"}{parentInfo}", _labelStyle);
                }
            }
            else
            {
                GUILayout.Label("<color=#888888>\u25a0 No active contexts</color>", _labelStyle);
            }

            GUILayout.Space(4);

            GUILayout.Label($"<color=#88ccff>\u25a0 Recent ({_logLines.Count} lines)</color>", _labelStyle);
            lock (_logLines)
            {
                int start = Math.Max(0, _logLines.Count - maxLogLines);
                for (int i = start; i < _logLines.Count; i++)
                {
                    GUILayout.Label(_logLines[i], _labelStyle);
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();

            float bottomY = contentY + contentHeight + 2;
            GUI.Box(new Rect(x, bottomY, width, 20), $"Press {toggleKey} to close", _boxStyle);
        }

        public void LogSignal(string signalName, string message = "")
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(signalColor)}>\u25b8 {signalName}</color> {message}");
                TrimLog();
            }
        }

        public void LogError(string message)
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(errorColor)}>\u2716 ERR:</color> {message}");
                TrimLog();
            }
        }

        public void LogWarning(string message)
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(warningColor)}>\u26a0 WARN:</color> {message}");
                TrimLog();
            }
        }

        private void TrimLog()
        {
            if (_logLines.Count > maxLogLines * 2)
                _logLines.RemoveRange(0, _logLines.Count - maxLogLines);
        }

        private void OnDestroy()
        {
            if (_bgTexture != null)
            {
                Destroy(_bgTexture);
                _bgTexture = null;
            }
        }
    }
}
