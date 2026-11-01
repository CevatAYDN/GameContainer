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

                        if (Application.isPlaying && isResolved.HasValue)
                        {
                            NexusInspectorGUI.DrawBadge(isResolved.Value ? "Resolved" : "Unresolved", isResolved.Value ? StatusType.Success : StatusType.Error);
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

            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Window", StatusType.Info, 22))
            {
                NexusWindow.Open();
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>The members the container injects, as InjectScanner finds them: base classes included, and each [Inject]
        /// method's parameters, which keep no value to check, so their IsResolved is null (#254).</summary>
        private static List<(string Name, Type Type, bool IsOptional, bool? IsResolved)> GetInjectedDependencies(NexusBehaviour nb)
        {
            var list = new List<(string, Type, bool, bool?)>();
            if (nb == null) return list;
            var scanned = InjectScanner.ScanMembers(nb.GetType());

            foreach (var f in scanned.Fields)
            {
                object val = null;
                if (Application.isPlaying)
                {
                    try { val = f.GetValue(nb); } catch { }
                }
                list.Add((f.Name, f.FieldType, f.IsDefined(typeof(OptionalInjectAttribute), false), val != null));
            }

            var properties = new HashSet<string>();
            foreach (var p in scanned.Properties)
            {
                if (!properties.Add(p.Name)) continue;
                object val = null;
                if (Application.isPlaying)
                {
                    try { val = p.GetValue(nb); } catch { }
                }
                list.Add((p.Name, p.PropertyType, p.IsDefined(typeof(OptionalInjectAttribute), false), val != null));
            }

            foreach (var m in scanned.Methods)
                foreach (var parameter in m.GetParameters())
                    list.Add(($"{m.Name}({parameter.Name})", parameter.ParameterType, parameter.IsDefined(typeof(OptionalInjectAttribute), false), null));

            return list;
        }
    }
}
