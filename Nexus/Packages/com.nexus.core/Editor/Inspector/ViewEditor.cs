using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(View), true)]
    public class ViewEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var view = (View)target;
            var viewType = view.GetType();
            var mediatorAttr = viewType.GetCustomAttribute<MediatorAttribute>();

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Nexus View Binding Inspector", EditorStyles.boldLabel);

            if (mediatorAttr != null)
            {
                EditorGUILayout.HelpBox($"Bound Mediator: {mediatorAttr.MediatorType.Name}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox($"Warning: No [Mediator(typeof(...))] attribute attached to '{viewType.Name}'. " +
                                       "This view will bind to Context without a mediator.", MessageType.Warning);
            }

            EditorGUILayout.Space(5);
            DrawDefaultInspector();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
