using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public class NexusDataInspectorWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private int _selectedContextIndex = 0;
        private Dictionary<string, bool> _foldoutStates = new();

        [MenuItem("Window/Nexus/Data Inspector")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusDataInspectorWindow>("Nexus Data Inspector");
            window.minSize = new Vector2(350, 450);
            window.Show();
        }

        private void OnGUI()
        {
            // Title
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                margin = new RectOffset(10, 10, 10, 10)
            };
            titleStyle.normal.textColor = new Color(0.3f, 0.8f, 1f);
            GUILayout.Label("Nexus Data Inspector", titleStyle);

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Play Mode is required to inspect runtime model/singleton state.", MessageType.Info);
                return;
            }

            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0)
            {
                EditorGUILayout.HelpBox("No active contexts found at runtime.", MessageType.Info);
                return;
            }

            // Dropdown selection for contexts
            string[] contextNames = new string[contexts.Count];
            for (int i = 0; i < contexts.Count; i++)
            {
                var ctx = contexts[i] as Context;
                string tag = ctx != null ? ctx.ScopeTag : null;
                contextNames[i] = string.IsNullOrEmpty(tag) ? $"RootContext (Index {i})" : $"{tag} Context";
            }

            if (_selectedContextIndex >= contexts.Count)
            {
                _selectedContextIndex = 0;
            }

            EditorGUILayout.BeginHorizontal();
            _selectedContextIndex = EditorGUILayout.Popup("Active Context", _selectedContextIndex, contextNames);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            DrawSeparator();
            EditorGUILayout.Space(10);

            var selectedContext = contexts[_selectedContextIndex] as Context;
            if (selectedContext == null)
            {
                EditorGUILayout.HelpBox("Failed to cast selected context.", MessageType.Error);
                return;
            }

            var singletons = selectedContext.Container.GetRegisteredSingletons();
            if (singletons == null || singletons.Count == 0)
            {
                EditorGUILayout.HelpBox("No resolved models or singletons found in this context's container.", MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var kvp in singletons)
            {
                Type boundType = kvp.Key;
                object instance = kvp.Value;

                if (instance == null) continue;

                // Don't show container itself or the context itself to avoid massive noise
                if (boundType == typeof(NexusDI) || boundType == typeof(Context) || boundType == typeof(IContext) || 
                    boundType == typeof(SignalBus) || boundType == typeof(ISignalBus) || boundType == typeof(HybridQueue) || 
                    boundType == typeof(CommandPoolManager) || boundType == typeof(ViewBinder))
                {
                    continue;
                }

                string foldoutKey = $"{_selectedContextIndex}_{boundType.FullName}";
                if (!_foldoutStates.TryGetValue(foldoutKey, out bool foldoutExpanded))
                {
                    _foldoutStates[foldoutKey] = false;
                }

                Type concreteType = instance.GetType();
                string displayName = boundType == concreteType ? boundType.Name : $"{boundType.Name} ({concreteType.Name})";

                _foldoutStates[foldoutKey] = EditorGUILayout.Foldout(_foldoutStates[foldoutKey], displayName, true, EditorStyles.foldoutHeader);

                if (_foldoutStates[foldoutKey])
                {
                    EditorGUI.indentLevel++;
                    DrawSingletonFieldsAndProperties(instance);
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(5);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSingletonFieldsAndProperties(object instance)
        {
            Type type = instance.GetType();

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (fields.Length == 0 && properties.Length == 0)
            {
                EditorGUILayout.LabelField("No fields or properties available.");
                return;
            }

            GUILayout.Label("Fields", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            foreach (var field in fields)
            {
                // Skip compiler generated fields for properties
                if (field.Name.Contains("<") && field.Name.Contains(">")) continue;
                DrawField(instance, field);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(3);

            GUILayout.Label("Properties", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            foreach (var prop in properties)
            {
                DrawProperty(instance, prop);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawField(object instance, FieldInfo field)
        {
            object value = field.GetValue(instance);
            Type type = field.FieldType;

            EditorGUI.BeginChangeCheck();
            object newValue = value;

            if (type == typeof(int))
            {
                newValue = EditorGUILayout.IntField(field.Name, (int)(value ?? 0));
            }
            else if (type == typeof(float))
            {
                newValue = EditorGUILayout.FloatField(field.Name, (float)(value ?? 0f));
            }
            else if (type == typeof(double))
            {
                newValue = EditorGUILayout.DoubleField(field.Name, (double)(value ?? 0.0));
            }
            else if (type == typeof(bool))
            {
                newValue = EditorGUILayout.Toggle(field.Name, (bool)(value ?? false));
            }
            else if (type == typeof(string))
            {
                newValue = EditorGUILayout.TextField(field.Name, (string)value);
            }
            else if (type.IsEnum)
            {
                newValue = EditorGUILayout.EnumPopup(field.Name, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));
            }
            else
            {
                // Read-only fallback for complex structures
                EditorGUILayout.LabelField(field.Name, value != null ? value.ToString() : "null");
            }

            if (EditorGUI.EndChangeCheck())
            {
                try
                {
                    Undo.RecordObject(instance as UnityEngine.Object, "Modify Model Field");
                    field.SetValue(instance, newValue);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private void DrawProperty(object instance, PropertyInfo prop)
        {
            if (prop.GetIndexParameters().Length > 0) return; // Skip indexers

            object value = null;
            try
            {
                value = prop.GetValue(instance);
            }
            catch
            {
                EditorGUILayout.LabelField(prop.Name, "<Unable to read property>");
                return;
            }

            Type type = prop.PropertyType;

            if (prop.CanWrite && prop.GetSetMethod(true) != null)
            {
                EditorGUI.BeginChangeCheck();
                object newValue = value;

                if (type == typeof(int))
                {
                    newValue = EditorGUILayout.IntField(prop.Name, (int)(value ?? 0));
                }
                else if (type == typeof(float))
                {
                    newValue = EditorGUILayout.FloatField(prop.Name, (float)(value ?? 0f));
                }
                else if (type == typeof(double))
                {
                    newValue = EditorGUILayout.DoubleField(prop.Name, (double)(value ?? 0.0));
                }
                else if (type == typeof(bool))
                {
                    newValue = EditorGUILayout.Toggle(prop.Name, (bool)(value ?? false));
                }
                else if (type == typeof(string))
                {
                    newValue = EditorGUILayout.TextField(prop.Name, (string)value);
                }
                else if (type.IsEnum)
                {
                    newValue = EditorGUILayout.EnumPopup(prop.Name, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));
                }
                else
                {
                    EditorGUILayout.LabelField(prop.Name, value != null ? value.ToString() : "null");
                }

                if (EditorGUI.EndChangeCheck())
                {
                    try
                    {
                        Undo.RecordObject(instance as UnityEngine.Object, "Modify Model Property");
                        prop.SetValue(instance, newValue, null);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
            else
            {
                // Read-only property
                EditorGUILayout.LabelField(prop.Name, value != null ? value.ToString() : "null");
            }
        }

        private void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
        }
    }
}
