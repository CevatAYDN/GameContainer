using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(ContextData))]
    public class ContextDataEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var data = (ContextData)target;

            EditorGUILayout.HelpBox("Context setup", MessageType.None);
            EditorGUILayout.HelpBox("This asset controls how a Root finds and configures its Context.", MessageType.Info);

            if (string.IsNullOrEmpty(data.ScopeTag))
            {
                EditorGUILayout.HelpBox("Scope tag is empty. The context will rely on root-based discovery and may bind more broadly than intended.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox($"Scope tag: {data.ScopeTag}", MessageType.Info);
            }

            if (data.AssemblyScopes == null || data.AssemblyScopes.Length == 0)
            {
                EditorGUILayout.HelpBox("No assembly scopes are assigned. Nexus will scan default assemblies instead, which is easier to start with but less deterministic for reusable packages.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox($"Assembly scopes: {string.Join(", ", data.AssemblyScopes)}", MessageType.None);
            }

            EditorGUILayout.HelpBox(data.EnableAutoDiscovery
                ? "Auto discovery is enabled. That makes setup easier, but you should still keep the scope and parent chain intentional."
                : "Auto discovery is disabled. The context must be reached through explicit registration or parent wiring.",
                data.EnableAutoDiscovery ? MessageType.Info : MessageType.Warning);
            DrawDefaultInspector();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
