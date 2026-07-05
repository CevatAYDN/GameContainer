using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(Root))]
    public class RootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var root = (Root)target;

            // Draw header banner
            EditorGUILayout.Space(5);
            var headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("Nexus Root Context", headerStyle);
            EditorGUILayout.Space(5);

            // Status Badge
            if (Application.isPlaying)
            {
                var badgeStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };

                if (root.IsInitialized)
                {
                    GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);
                    GUILayout.Box("STATUS: INITIALIZED & ACTIVE", badgeStyle, GUILayout.ExpandWidth(true), GUILayout.Height(25));
                }
                else
                {
                    GUI.backgroundColor = new Color(0.9f, 0.5f, 0.1f);
                    GUILayout.Box("STATUS: INITIALIZING...", badgeStyle, GUILayout.ExpandWidth(true), GUILayout.Height(25));
                }
                GUI.backgroundColor = Color.white;

                if (root.Context != null)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox($"Active Singletons: {root.Context.Container.ActiveSingletonsCount}\n" +
                                           $"Registered Commands: {root.Context.SignalBusInternal.CommandHandlers.Count}\n" +
                                           $"Scope Tag: {root.Context.ScopeTag ?? "Global"}", MessageType.Info);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Root initialized during Awake/Start when Play Mode starts.", MessageType.None);
            }

            EditorGUILayout.Space(10);
            DrawDefaultInspector();

            EditorGUILayout.Space(15);
            if (GUILayout.Button("Open Nexus Dashboard", GUILayout.Height(30)))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
