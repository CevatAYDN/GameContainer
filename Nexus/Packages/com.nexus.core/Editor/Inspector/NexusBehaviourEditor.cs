using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(NexusBehaviour), true)]
    public class NexusBehaviourEditor : UnityEditor.Editor
    {
        private SerializedProperty _autoInjectProp;
        private bool _showDepsFoldout = true;

        private void OnEnable()
        {
            EnsureProperties();
        }

        private bool EnsureProperties()
        {
            if (target == null) return false;
            try
            {
                _autoInjectProp ??= serializedObject.FindProperty("_autoInject");
                return _autoInjectProp != null;
            }
            catch
            {
                return false;
            }
        }

        public override void OnInspectorGUI()
        {
            if (target == null || !EnsureProperties()) return;

            try
            {
                serializedObject.Update();
            }
            catch
            {
                return;
            }
            var nb = (NexusBehaviour)target;
            bool isAutoInject = nb.AutoInject;

            NexusInspectorGUI.DrawHeader(target.GetType().Name, "Nexus Rapid Reactive Component",
                isAutoInject ? "Auto-Inject ON" : "Manual Inject",
                isAutoInject ? StatusType.Success : StatusType.Warning);

            // 1. Nexus Configuration Card
            NexusInspectorGUI.BeginCard("Nexus Settings");
            EditorGUILayout.PropertyField(_autoInjectProp, new GUIContent("Auto Inject When Ready", "Injects after the owning context finishes configuring. Start hooks wait for asynchronous startup."));
            NexusInspectorGUI.EndCard();

            // 2. Component Properties (user fields)
            NexusInspectorGUI.BeginCard("Component Properties");
            DrawPropertiesExcluding(serializedObject, "m_Script", "_autoInject");
            NexusInspectorGUI.EndCard();

            // 3. Detected Injected Dependencies
            var deps = GetInjectedDependencies(nb);
            NexusInspectorGUI.BeginCard("Dependencies", $"{deps.Count} Declared", deps.Count > 0 ? StatusType.Info : StatusType.Warning);

            if (deps.Count > 0)
            {
                _showDepsFoldout = EditorGUILayout.Foldout(_showDepsFoldout, "Injected Contract Dependencies", true);
                if (_showDepsFoldout)
                {
                    EditorGUI.indentLevel++;
                    foreach (var (name, type, isOptional, isResolved) in deps)
                    {
                        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                        string attrLabel = isOptional ? "[OptionalInject]" : "[Inject]";
                        EditorGUILayout.LabelField($"{attrLabel} {name} : {type.Name}", EditorStyles.boldLabel);

                        if (Application.isPlaying)
                        {
                            NexusInspectorGUI.DrawBadge(isResolved ? "Resolved" : "Unresolved", isResolved ? StatusType.Success : StatusType.Error);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                NexusInspectorGUI.DrawMessage("No [Inject] members declared on this component. Use [Inject] public IService MyService { get; set; } to receive singletons.", StatusType.Info);
            }
            NexusInspectorGUI.EndCard();

            // 4. Quick Actions
            NexusInspectorGUI.BeginCard("Actions");
            if (NexusInspectorGUI.DrawActionButton("⚡ Trigger Injection Now", StatusType.Info, 24))
            {
                nb.Inject();
            }

            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Dashboard", StatusType.Info, 22))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        private static List<(string Name, Type Type, bool IsOptional, bool IsResolved)> GetInjectedDependencies(NexusBehaviour nb)
        {
            var list = new List<(string, Type, bool, bool)>();
            if (nb == null) return list;
            var targetType = nb.GetType();

            foreach (var f in targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                bool isOpt = f.IsDefined(typeof(OptionalInjectAttribute), true);
                bool isReq = f.IsDefined(typeof(InjectAttribute), true);
                if (isReq || isOpt)
                {
                    object val = null;
                    if (Application.isPlaying)
                    {
                        try { val = f.GetValue(nb); } catch { }
                    }
                    list.Add((f.Name, f.FieldType, isOpt, val != null));
                }
            }

            foreach (var p in targetType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                bool isOpt = p.IsDefined(typeof(OptionalInjectAttribute), true);
                bool isReq = p.IsDefined(typeof(InjectAttribute), true);
                if (isReq || isOpt)
                {
                    object val = null;
                    if (Application.isPlaying)
                    {
                        try { val = p.GetValue(nb); } catch { }
                    }
                    list.Add((p.Name, p.PropertyType, isOpt, val != null));
                }
            }

            return list;
        }
    }
}
