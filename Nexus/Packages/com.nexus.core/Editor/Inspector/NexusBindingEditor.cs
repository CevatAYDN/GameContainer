using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;
using Nexus.Core.Components;

namespace Nexus.Editor.Inspector
{
    [CustomEditor(typeof(NexusBinding))]
    public class NexusBindingEditor : UnityEditor.Editor
    {
        private SerializedProperty _scopeProp;
        private SerializedProperty _timeProp;
        private SerializedProperty _customTargetsProp;
        private bool _showTargetsFoldout = true;

        private void OnEnable()
        {
            EnsureProperties();
        }

        private bool EnsureProperties()
        {
            if (target == null) return false;
            try
            {
                _scopeProp ??= serializedObject.FindProperty("_scope");
                _timeProp ??= serializedObject.FindProperty("_time");
                _customTargetsProp ??= serializedObject.FindProperty("_customTargets");
                return _scopeProp != null;
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
            var binding = (NexusBinding)target;

            string timingStr = binding.Time.ToString();
            NexusInspectorGUI.DrawHeader("Nexus Binding", "Dependency Injection Bridge", timingStr, StatusType.Info);

            // 1. Settings Card
            NexusInspectorGUI.BeginCard("Configuration", "Active", StatusType.Success);
            EditorGUILayout.PropertyField(_timeProp, new GUIContent("Trigger Timing", "When dependency injection executes automatically."));
            EditorGUILayout.PropertyField(_scopeProp, new GUIContent("Target Scope", "Which components are scanned for [Inject] attributes."));

            EditorGUILayout.PropertyField(_customTargetsProp, new GUIContent("Custom Targets (Optional)", "Explicit components to inject. If populated, overrides Target Scope."));
            NexusInspectorGUI.EndCard();

            // 2. Discovered Dependencies Preview Card
            var injectables = DiscoverInjectables(binding);
            NexusInspectorGUI.BeginCard("Injectable Targets", $"{injectables.Count} Found", injectables.Count > 0 ? StatusType.Success : StatusType.Warning);

            if (injectables.Count > 0)
            {
                _showTargetsFoldout = EditorGUILayout.Foldout(_showTargetsFoldout, "Target Components & Injected Members", true);
                if (_showTargetsFoldout)
                {
                    EditorGUI.indentLevel++;
                    foreach (var (comp, members) in injectables)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.LabelField($"• {comp.GetType().Name} ({comp.gameObject.name})", EditorStyles.boldLabel);
                        foreach (var m in members)
                        {
                            EditorGUILayout.LabelField($"   [Inject] {m.Name} ({m.Type.Name})", EditorStyles.miniLabel);
                        }
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                NexusInspectorGUI.DrawMessage("No components with [Inject] or [OptionalInject] members were found within the selected scope. Add [Inject] to your MonoBehaviour fields/properties to receive services.", StatusType.Info);
            }
            NexusInspectorGUI.EndCard();

            // 3. Runtime & Actions Card
            NexusInspectorGUI.BeginCard("Actions & Diagnostics");
            if (Application.isPlaying)
            {
                NexusInspectorGUI.DrawStatusRow("Runtime State", "PlayMode Active", StatusType.Success);
            }

            if (NexusInspectorGUI.DrawActionButton("⚡ Run Injection Now", StatusType.Info, 26))
            {
                binding.InjectNow();
                EditorUtility.SetDirty(binding.gameObject);
            }

            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Dashboard", StatusType.Info, 22))
            {
                EditorApplication.ExecuteMenuItem("Window/Nexus/Dashboard %#n");
            }
            NexusInspectorGUI.EndCard();

            serializedObject.ApplyModifiedProperties();
        }

        internal static List<(MonoBehaviour Component, List<(string Name, Type Type)> InjectedMembers)> DiscoverInjectables(NexusBinding binding)
        {
            var results = new List<(MonoBehaviour, List<(string, Type)>)>();
            if (binding == null) return results;

            using var serializedBinding = new SerializedObject(binding);
            var customTargets = serializedBinding.FindProperty("_customTargets");
            bool hasCustomTargets = customTargets != null && customTargets.arraySize > 0;
            MonoBehaviour[] candidates;
            if (hasCustomTargets)
            {
                candidates = new MonoBehaviour[customTargets.arraySize];
                for (int i = 0; i < candidates.Length; i++)
                    candidates[i] = customTargets.GetArrayElementAtIndex(i).objectReferenceValue as MonoBehaviour;
            }
            else candidates = binding.Scope switch
            {
                InjectionScope.Self => binding.GetComponents<MonoBehaviour>(),
                InjectionScope.Children => binding.GetComponentsInChildren<MonoBehaviour>(true),
                InjectionScope.Hierarchy => binding.GetComponentsInChildren<MonoBehaviour>(true),
                _ => Array.Empty<MonoBehaviour>()
            };

            foreach (var comp in candidates)
            {
                if (comp == null) continue;
                if (!hasCustomTargets && (comp == binding ||
                    (binding.Scope == InjectionScope.Children && comp.gameObject == binding.gameObject))) continue;
                var members = new List<(string, Type)>();
                var type = comp.GetType();

                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.IsDefined(typeof(InjectAttribute), true) || f.IsDefined(typeof(OptionalInjectAttribute), true))
                    {
                        members.Add((f.Name, f.FieldType));
                    }
                }

                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.IsDefined(typeof(InjectAttribute), true) || p.IsDefined(typeof(OptionalInjectAttribute), true))
                    {
                        members.Add((p.Name, p.PropertyType));
                    }
                }

                if (members.Count > 0)
                {
                    results.Add((comp, members));
                }
            }

            return results;
        }
    }
}
