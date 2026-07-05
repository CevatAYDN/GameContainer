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

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Nexus Context Data Configuration", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(data.ScopeTag))
            {
                EditorGUILayout.HelpBox("ScopeTag is empty. Defaulting to root context settings.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox($"Scope: {data.ScopeTag}", MessageType.None);
            }

            EditorGUILayout.Space(5);
            DrawDefaultInspector();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
