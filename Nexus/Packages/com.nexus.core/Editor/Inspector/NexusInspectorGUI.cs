using System;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor.Inspector
{
    public enum StatusType
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Shared IMGUI design system and UI helpers for all Nexus component inspectors.
    /// Provides sleek, modern, card-based inspector layouts with consistent colors,
    /// status badges, and quick-action buttons.
    /// </summary>
    public static class NexusInspectorGUI
    {
        private static bool s_stylesInitialized;
        private static GUIStyle s_headerBoxStyle;
        private static GUIStyle s_titleStyle;
        private static GUIStyle s_subtitleStyle;
        private static GUIStyle s_cardStyle;
        private static GUIStyle s_cardHeaderStyle;
        private static GUIStyle s_labelStyle;
        private static GUIStyle s_valueStyle;

        private static readonly Color AccentBlue = new(0.20f, 0.65f, 1.00f);
        private static readonly Color AccentGreen = new(0.00f, 0.85f, 0.45f);
        private static readonly Color AccentYellow = new(1.00f, 0.80f, 0.25f);
        private static readonly Color AccentRed = new(0.95f, 0.30f, 0.30f);

        private static void EnsureStyles()
        {
            if (s_stylesInitialized && s_headerBoxStyle != null) return;

            s_headerBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 8, 8),
                margin = new RectOffset(0, 0, 0, 8)
            };

            s_titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                normal = { textColor = AccentBlue }
            };

            s_subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.70f, 0.72f, 0.78f) }
            };

            s_cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 8)
            };

            s_cardHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.90f, 0.92f, 0.96f) }
            };

            s_labelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.75f, 0.78f, 0.85f) }
            };

            s_valueStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };

            s_stylesInitialized = true;
        }

        public static void DrawHeader(string title, string subtitle, string badge = null, StatusType badgeType = StatusType.Info)
        {
            EnsureStyles();

            EditorGUILayout.BeginVertical(s_headerBoxStyle);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(title, s_titleStyle);
            if (!string.IsNullOrEmpty(subtitle))
            {
                EditorGUILayout.LabelField(subtitle, s_subtitleStyle);
            }
            EditorGUILayout.EndVertical();

            if (!string.IsNullOrEmpty(badge))
            {
                GUILayout.FlexibleSpace();
                DrawBadge(badge, badgeType);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        public static void BeginCard(string header = null, string badge = null, StatusType badgeType = StatusType.Info)
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical(s_cardStyle);

            if (!string.IsNullOrEmpty(header) || !string.IsNullOrEmpty(badge))
            {
                EditorGUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(header))
                {
                    EditorGUILayout.LabelField(header, s_cardHeaderStyle);
                }
                if (!string.IsNullOrEmpty(badge))
                {
                    GUILayout.FlexibleSpace();
                    DrawBadge(badge, badgeType);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);
            }
        }

        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
        }

        public static void DrawStatusRow(string label, string status, StatusType type = StatusType.Info)
        {
            EnsureStyles();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, s_labelStyle, GUILayout.Width(130));
            DrawBadge(status, type);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawKeyValue(string label, string value)
        {
            EnsureStyles();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, s_labelStyle, GUILayout.Width(130));
            EditorGUILayout.LabelField(value, s_valueStyle);
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawBadge(string text, StatusType type)
        {
            EnsureStyles();
            Color color = GetColorForType(type);
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = color;
            GUILayout.Label(text, EditorStyles.helpBox, GUILayout.ExpandWidth(false));
            GUI.backgroundColor = prevBg;
        }

        public static void DrawMessage(string message, StatusType type)
        {
            MessageType msgType = type switch
            {
                StatusType.Success => MessageType.Info,
                StatusType.Warning => MessageType.Warning,
                StatusType.Error => MessageType.Error,
                _ => MessageType.None
            };
            EditorGUILayout.HelpBox(message, msgType);
        }

        public static bool DrawActionButton(string text, StatusType type = StatusType.Info, float height = 24)
        {
            EnsureStyles();
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = GetColorForType(type);
            bool clicked = GUILayout.Button(text, GUILayout.Height(height));
            GUI.backgroundColor = prevBg;
            return clicked;
        }

        private static Color GetColorForType(StatusType type) => type switch
        {
            StatusType.Success => AccentGreen,
            StatusType.Warning => AccentYellow,
            StatusType.Error => AccentRed,
            _ => AccentBlue
        };
    }
}
