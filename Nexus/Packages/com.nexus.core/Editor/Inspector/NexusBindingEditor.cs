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
        // The preview's discovery, kept until something it reads changes: it ran on every IMGUI event, each time a
        // GetComponentsInChildren and a member scan per component (#261). Null: discover on the next draw.
        private List<(MonoBehaviour Component, List<(string Name, Type Type)> InjectedMembers)> _injectables;

        private void OnEnable()
        {
            EnsureProperties();
            _injectables = null;
            // A child created, destroyed or moved; a component added or removed, or a property edited (undoable changes);
            // an undo.
            EditorApplication.hierarchyChanged += InvalidateInjectables;
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += InvalidateInjectables;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= InvalidateInjectables;
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            Undo.undoRedoPerformed -= InvalidateInjectables;
        }

        private void InvalidateInjectables() => _injectables = null;

        private void OnChangesPublished(ref ObjectChangeEventStream stream) => _injectables = null;

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
            // A component destroyed alone (Destroy in Play Mode) raises none of the events above.
            if (_injectables != null && _injectables.Exists(item => item.Component == null)) _injectables = null;
            var injectables = _injectables ??= DiscoverInjectables(binding);
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

            if (NexusInspectorGUI.DrawActionButton("🔍 Open Nexus Window", StatusType.Info, 22))
            {
                NexusWindow.Open();
            }
            NexusInspectorGUI.EndCard();

            if (serializedObject.ApplyModifiedProperties()) _injectables = null; // the scope or the custom targets changed
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
                // The members the container injects, as InjectScanner finds them: the leaf type's GetFields missed a base
                // class's private members, and [Inject] methods were not read (#254).
                var members = new List<(string, Type)>();
                var scanned = InjectScanner.ScanMembers(comp.GetType());
                foreach (var f in scanned.Fields)
                    members.Add((f.Name, f.FieldType));
                var properties = new HashSet<string>();
                foreach (var p in scanned.Properties)
                    if (properties.Add(p.Name)) members.Add((p.Name, p.PropertyType));
                foreach (var m in scanned.Methods)
                    foreach (var parameter in m.GetParameters())
                        members.Add(($"{m.Name}({parameter.Name})", parameter.ParameterType));

                if (members.Count > 0)
                {
                    results.Add((comp, members));
                }
            }

            return results;
        }
    }
}
