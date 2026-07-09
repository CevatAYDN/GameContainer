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
            var data = root.ContextData;
            bool hasContext = root.Context != null;
            bool hasContextData = data != null;
            bool hasParent = root.ParentRoot != null;
            bool hasAutoDiscovery = hasContextData && data.EnableAutoDiscovery;

            EditorGUILayout.HelpBox("Root health", MessageType.None);
            DrawStatusLine("Context data", hasContextData ? data.name : "Missing", hasContextData ? MessageType.Info : MessageType.Error);
            DrawStatusLine("Parent root", hasParent ? root.ParentRoot.name : "None", hasParent ? MessageType.Info : MessageType.Warning);
            DrawStatusLine("Context", hasContext ? "Bound" : "Not bound", hasContext ? MessageType.Info : MessageType.Error);
            if (hasContextData)
                DrawStatusLine("Discovery", hasAutoDiscovery ? "Auto discovery on" : "Manual registration expected", hasAutoDiscovery ? MessageType.Info : MessageType.Warning);

            if (Application.isPlaying)
            {
                if (root.IsInitialized)
                {
                    EditorGUILayout.HelpBox("Status: Initialized and active", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("Status: Starting up", MessageType.Warning);
                }

                if (hasContext)
                {
                    EditorGUILayout.Space(5);
                    var lifecycleCount = root.Context.Container.IsRegistered(typeof(IContextLifecycle)) ? 1 : 0;
                    EditorGUILayout.HelpBox($"Bound services: {root.Context.Container.ActiveSingletonsCount}\n" +
                                           $"Command handlers: {root.Context.SignalBusInternal.CommandHandlers.Count}\n" +
                                           $"Scope tag: {root.Context.ScopeTag ?? "Global"}\n" +
                                           $"Lifecycle: {(lifecycleCount > 0 ? "Registered" : "Not registered")}", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.HelpBox("Root does not have an active Context yet. Check the assigned ContextData, parent root order, and lifecycle registration.", MessageType.Error);
                }
            }
            else
            {
                if (!hasContextData)
                {
                    EditorGUILayout.HelpBox("Assign a ContextData asset before testing this root.", MessageType.Error);
                }
                else if (!hasParent && !hasAutoDiscovery)
                {
                    EditorGUILayout.HelpBox("This root will need explicit lifecycle registration or a parent root to resolve cleanly.", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox("This root initializes when Play Mode starts. Keep the data asset and parent chain valid before testing.", MessageType.Info);
                }
            }

            if (!Application.isPlaying && hasContextData)
            {
                EditorGUILayout.HelpBox($"Auto discovery: {(data.EnableAutoDiscovery ? "On" : "Off")}\nAssembly scopes: {(data.AssemblyScopes == null || data.AssemblyScopes.Length == 0 ? "Default scan" : string.Join(", ", data.AssemblyScopes))}", MessageType.None);
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

        private static void DrawStatusLine(string label, string value, MessageType messageType)
        {
            EditorGUILayout.HelpBox($"{label}: {value}", messageType);
        }
    }
}
