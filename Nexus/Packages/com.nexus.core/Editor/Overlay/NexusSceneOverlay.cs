using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Scene-view overlay for Nexus contexts. Draws, via <see cref="SceneView.duringSceneGui"/>:
    /// - a wireframe "context boundary" cube around every <see cref="Root"/> GameObject
    ///   hierarchy, colored per scope tag, with a scope-tag label;
    /// - a dotted parent→child link between Root GameObjects (context hierarchy);
    /// - labeled signal spokes inside each live context (signal → command wiring);
    /// - inter-context links for <c>[CrossContext]</c> signals (broadcast source → target).
    ///
    /// Rendering reads a throttled, cached model built by <see cref="NexusSceneOverlayData"/>
    /// so the per-repaint cost stays at a few draw calls. Toggle: Tools → Nexus → Scene Overlay.
    /// </summary>
    [InitializeOnLoad]
    public static class NexusSceneOverlay
    {
        private const string k_EnabledPref = "Nexus.SceneOverlay.Enabled";
        private const string k_MenuItem = "Tools/Nexus/Scene Overlay";
        // Data is live only during Play Mode (registrations + bounds move); a half-second
        // refresh is plenty and keeps edit-mode repaints cheap.
        private const double k_RefreshInterval = 0.5;
        // Cap the per-context spoke fan-out so a context with dozens of handlers stays readable.
        private const int k_MaxSpokesPerContext = 24;

        private static readonly List<NexusSceneOverlayContextNode> s_nodes = new();
        private static int s_lastSignature = int.MinValue;
        private static double s_lastRefresh;
        private static double s_lastErrorLog;

        private static readonly GUIStyle s_scopeLabelStyle;
        private static readonly GUIStyle s_linkLabelStyle;

        static NexusSceneOverlay()
        {
            // Styles must not touch GUI.skin: [InitializeOnLoad] static constructors run
            // outside any OnGUI context and GUI.skin access there throws.
            s_scopeLabelStyle = new GUIStyle
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            s_linkLabelStyle = new GUIStyle
            {
                fontSize = 9,
                alignment = TextAnchor.MiddleLeft,
            };

            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>Master switch. Toggled from Tools → Nexus → Scene Overlay (default ON).</summary>
        public static bool Enabled
        {
            get => SessionState.GetBool(k_EnabledPref, true);
            set
            {
                SessionState.SetBool(k_EnabledPref, value);
                SceneView.RepaintAll();
            }
        }

        [MenuItem(k_MenuItem)]
        private static void ToggleEnabled() => Enabled = !Enabled;

        [MenuItem(k_MenuItem, true)]
        private static bool ToggleEnabled_Validate()
        {
            Menu.SetChecked(k_MenuItem, Enabled);
            return true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            s_lastRefresh = 0; // force an immediate refresh on the next update tick
        }

        private static void OnEditorUpdate()
        {
            if (!Enabled) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastRefresh < k_RefreshInterval) return;
            s_lastRefresh = now;
            RefreshData();
        }

        /// <summary>Rebuilds the cached model and repaints only when the scene wiring changed.</summary>
        private static void RefreshData()
        {
            try
            {
                var roots = Object.FindObjectsByType<Root>(FindObjectsSortMode.None);
                var nodes = NexusSceneOverlayData.CollectContextNodes(roots);
                int signature = ComputeSignature(nodes);
                if (signature == s_lastSignature) return;

                s_lastSignature = signature;
                s_nodes.Clear();
                s_nodes.AddRange(nodes);

                SceneView.RepaintAll();
            }
            catch (System.Exception ex)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - s_lastErrorLog > 5.0)
                {
                    s_lastErrorLog = now;
                    Debug.LogWarning($"[NexusSceneOverlay] Data refresh failed (overlay keeps the last good frame): {ex.Message}");
                }
            }
        }

        private static int ComputeSignature(List<NexusSceneOverlayContextNode> nodes)
        {
            int hash = 17;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                hash = hash * 31 + (n.ScopeTag?.GetHashCode() ?? 0);
                hash = hash * 31 + n.Bounds.center.GetHashCode();
                hash = hash * 31 + n.Bounds.size.GetHashCode();
                hash = hash * 31 + n.Links.Count;
                hash = hash * 31 + (n.ParentGameObject != null ? n.ParentGameObject.GetHashCode() : 0);
            }
            return hash;
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            if (!Enabled) return;

            Handles.BeginGUI();
            try
            {
                for (int i = 0; i < s_nodes.Count; i++)
                    DrawContextNode(s_nodes[i]);
                for (int i = 0; i < s_nodes.Count; i++)
                    DrawCrossContextLinks(s_nodes[i]);
            }
            finally
            {
                Handles.EndGUI();
            }
        }

        private static Color ColorFor(NexusSceneOverlayContextNode node)
            => NexusSceneOverlayData.Palette[node.ColorIndex % NexusSceneOverlayData.Palette.Length];

        private static void DrawContextNode(NexusSceneOverlayContextNode node)
        {
            var color = ColorFor(node);
            var go = node.GameObject;
            if (go == null) return;

            // Context boundary: merged hierarchy bounds.
            Handles.color = color;
            Handles.DrawWireCube(node.Bounds.center, node.Bounds.size);

            // Scope label above the boundary.
            s_scopeLabelStyle.normal.textColor = color;
            var labelPos = new Vector3(node.Bounds.center.x, node.Bounds.max.y + 0.15f, node.Bounds.center.z);
            Handles.Label(labelPos, node.ScopeTag, s_scopeLabelStyle);

            // Parent → child context link (dotted so it reads as a hierarchy, not a signal).
            if (node.ParentGameObject != null)
            {
                var parentColor = color * 0.75f;
                parentColor.a = 1f;
                Handles.color = parentColor;
                Handles.DrawDottedLine(go.transform.position, node.ParentGameObject.transform.position, 3f);
            }

            // Signal wiring spokes (Play Mode only — Edit Mode has no registrations).
            if (node.Links == null || node.Links.Count == 0) return;
            var origin = go.transform.position;
            int spokeCount = System.Math.Min(node.Links.Count, k_MaxSpokesPerContext);
            float baseAngle = node.ColorIndex * 24f;
            for (int i = 0; i < spokeCount; i++)
            {
                var link = node.Links[i];
                float angle = (baseAngle + i * (360f / k_MaxSpokesPerContext)) * Mathf.Deg2Rad;
                float dist = 1.5f + (i % 5) * 1.1f;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var end = origin + dir * dist;

                Handles.color = color * 0.85f;
                Handles.DrawLine(origin, end);

                s_linkLabelStyle.normal.textColor = color;
                Handles.Label(end, $"{link.SignalName}→{link.HandlerName}", s_linkLabelStyle);
            }
            if (node.Links.Count > k_MaxSpokesPerContext)
            {
                s_linkLabelStyle.normal.textColor = Color.white;
                Handles.Label(origin + Vector3.up * 0.4f, $"+{node.Links.Count - k_MaxSpokesPerContext} more", s_linkLabelStyle);
            }
        }

        /// <summary>
        /// Draws [CrossContext] signal links from this context to its broadcast targets: any
        /// other context that (a) falls under the optional scope restriction and (b) registered
        /// a handler for the same signal type.
        /// </summary>
        private static void DrawCrossContextLinks(NexusSceneOverlayContextNode node)
        {
            if (node.Links == null || node.Links.Count == 0) return;
            var origin = node.GameObject != null ? node.GameObject.transform.position : node.Bounds.center;

            for (int i = 0; i < node.Links.Count; i++)
            {
                var link = node.Links[i];
                if (!link.IsCrossContext) continue;

                for (int t = 0; t < s_nodes.Count; t++)
                {
                    var target = s_nodes[t];
                    if (target == node) continue;
                    if (!string.IsNullOrEmpty(link.CrossContextScope) &&
                        !string.Equals(target.ScopeTag, link.CrossContextScope, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (target.GameObject == null || !HasHandler(target, link.SignalName)) continue;

                    var targetPos = target.GameObject.transform.position;
                    Handles.color = ColorFor(target);
                    Handles.DrawLine(origin, targetPos);

                    s_linkLabelStyle.normal.textColor = ColorFor(target);
                    Handles.Label(Vector3.Lerp(origin, targetPos, 0.5f) + Vector3.up * 0.15f, link.SignalName, s_linkLabelStyle);
                }
            }
        }

        private static bool HasHandler(NexusSceneOverlayContextNode node, string signalName)
        {
            if (node.Links == null) return false;
            for (int i = 0; i < node.Links.Count; i++)
                if (string.Equals(node.Links[i].SignalName, signalName, System.StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
