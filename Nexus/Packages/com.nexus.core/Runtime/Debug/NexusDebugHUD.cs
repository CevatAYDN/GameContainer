using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// In-game debug overlay for Nexus. Toggle with F12.
    /// Shows active contexts, recent signal traffic, and command errors.
    ///
    /// Attach to any GameObject in the scene or use the Nexus Wizard to add it.
    /// </summary>
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

            // FPS counter
            _frameCount++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _frameCount / _fpsTimer;
                _frameCount = 0;
                _fpsTimer = 0;
            }
        }

        private void OnGUI()
        {
            if (!_isVisible) return;

            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = fontSize,
                normal = { textColor = textColor, background = MakeTex(2, 2, backgroundColor) },
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = true
            };

            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                richText = true,
                normal = { textColor = textColor }
            };

            float width = 420;
            float x = 10;
            float y = 10;

            // Header
            string header = $"<b>NEXUS DEBUG</b>  FPS: {_fps:F1}  |  {DateTime.Now:HH:mm:ss}";
            float headerHeight = 24;
            GUI.Box(new Rect(x, y, width, headerHeight), header, boxStyle);

            float contentY = y + headerHeight + 2;
            float contentHeight = 300;

            // Content scroll area
            GUILayout.BeginArea(new Rect(x, contentY, width, contentHeight), boxStyle);
            GUILayout.BeginVertical();

            // Active contexts
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts != null && contexts.Count > 0)
            {
                GUILayout.Label($"<color=#66ff66>■ Contexts:</color> {contexts.Count} active", labelStyle);
                foreach (var ctx in contexts)
                {
                    string parentInfo = ctx.Parent != null ? $" → parent: {ctx.Parent.ScopeTag}" : " (root)";
                    GUILayout.Label($"  {ctx.ScopeTag ?? "(no tag)"}{parentInfo}", labelStyle);
                }
            }
            else
            {
                GUILayout.Label("<color=#888888>■ No active contexts</color>", labelStyle);
            }

            GUILayout.Space(4);

            // Recent log
            GUILayout.Label($"<color=#88ccff>■ Recent ({_logLines.Count} lines)</color>", labelStyle);
            lock (_logLines)
            {
                int start = Math.Max(0, _logLines.Count - maxLogLines);
                for (int i = start; i < _logLines.Count; i++)
                {
                    GUILayout.Label(_logLines[i], labelStyle);
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();

            // Close hint at bottom
            float bottomY = contentY + contentHeight + 2;
            GUI.Box(new Rect(x, bottomY, width, 20), $"Press {toggleKey} to close", boxStyle);
        }

        /// <summary>
        /// Logs a signal-related message to the HUD buffer.
        /// Call from commands or mediators for real-time feedback.
        /// </summary>
        public void LogSignal(string signalName, string message = "")
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(signalColor)}>▸ {signalName}</color> {message}");
                TrimLog();
            }
        }

        /// <summary>
        /// Logs an error to the HUD buffer.
        /// </summary>
        public void LogError(string message)
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(errorColor)}>✖ ERR:</color> {message}");
                TrimLog();
            }
        }

        /// <summary>
        /// Logs a warning to the HUD buffer.
        /// </summary>
        public void LogWarning(string message)
        {
            lock (_logLines)
            {
                _logLines.Add($"<color=#{ColorUtility.ToHtmlStringRGB(warningColor)}>⚠ WARN:</color> {message}");
                TrimLog();
            }
        }

        private void TrimLog()
        {
            if (_logLines.Count > maxLogLines * 2)
                _logLines.RemoveRange(0, _logLines.Count - maxLogLines);
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}
