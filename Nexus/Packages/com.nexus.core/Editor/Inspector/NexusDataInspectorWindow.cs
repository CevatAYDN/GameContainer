using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Editor window that inspects runtime Nexus data, including active contexts, their containers,
    /// registered singletons, and injectable field values. Auto-refreshes during Play Mode.
    /// Accessed via Window/Nexus/Data Inspector.
    /// </summary>
    public class NexusDataInspectorWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private int _selectedContextIndex;
        private readonly Dictionary<string, bool> _foldoutStates = new();
        private double _lastAutoRefresh;
        private const double AutoRefreshInterval = 1.0;
        private string _searchFilter = "";
        private int _lastKnownContextCount = -1;

        private static readonly Color TitleColor = new Color(0.3f, 0.8f, 1f);
        private static readonly Color SeparatorColor = new Color(0.3f, 0.3f, 0.3f);
        private GUIStyle _titleStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _miniBoldLabelStyle;

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                margin = new RectOffset(10, 10, 10, 10)
            };
            _titleStyle.normal.textColor = TitleColor;
            _headerStyle = new GUIStyle(EditorStyles.foldoutHeader);
            _miniBoldLabelStyle = new GUIStyle(EditorStyles.miniBoldLabel);
        }

        [MenuItem("Window/Nexus/Data Inspector")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusDataInspectorWindow>("Nexus Data Inspector");
            window.minSize = new Vector2(350, 450);
            window.Show();
        }

        private void OnGUI()
        {
            EnsureStyles();
            GUILayout.Label("Nexus Data Inspector", _titleStyle);

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Play Mode is required to inspect runtime model/singleton state.", MessageType.Info);
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastAutoRefresh > AutoRefreshInterval)
            {
                _lastAutoRefresh = EditorApplication.timeSinceStartup;
                Repaint();
            }

            // Snapshot to avoid enumeration modifications
            var contextsSnapshot = new List<IContext>(NexusRuntime.ActiveContexts);
            if (contextsSnapshot.Count == 0)
            {
                EditorGUILayout.HelpBox("No active contexts found at runtime.", MessageType.Info);
                return;
            }

            // Track context count changes to auto-reset stale foldout state
            _lastKnownContextCount = contextsSnapshot.Count;

            // Context selector
            string[] contextNames = new string[contextsSnapshot.Count];
            for (int i = 0; i < contextsSnapshot.Count; i++)
            {
                if (contextsSnapshot[i] is Context ctx)
                {
                    string tag = ctx.ScopeTag;
                    contextNames[i] = string.IsNullOrEmpty(tag) ? $"RootContext (Index {i})" : $"{tag} Context";
                }
                else
                {
                    contextNames[i] = $"Context (Index {i})";
                }
            }

            if (_selectedContextIndex >= contextsSnapshot.Count)
                _selectedContextIndex = 0;

            EditorGUILayout.BeginHorizontal();
            _selectedContextIndex = EditorGUILayout.Popup("Active Context", _selectedContextIndex, contextNames);
            EditorGUILayout.EndHorizontal();

            // Search filter
            EditorGUILayout.BeginHorizontal();
            _searchFilter = EditorGUILayout.TextField("Search", _searchFilter);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            DrawSeparator();
            EditorGUILayout.Space(10);

            var selectedContext = contextsSnapshot[_selectedContextIndex] as Context;
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

            bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);
            foreach (var kvp in singletons)
            {
                Type boundType = kvp.Key;
                object instance = kvp.Value;
                if (instance == null) continue;

                if (boundType == typeof(NexusDI) || boundType == typeof(Context) || boundType == typeof(IContext) ||
                    boundType == typeof(SignalBus) || boundType == typeof(ISignalBus) || boundType == typeof(HybridQueue) ||
                    boundType == typeof(CommandPoolManager) || boundType == typeof(ViewBinder))
                    continue;

                // Apply search filter
                if (hasFilter)
                {
                    string name = boundType.Name;
                    string fullName = boundType.FullName;
                    if (name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        fullName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                string foldoutKey = $"{_selectedContextIndex}_{boundType.FullName}";
                if (!_foldoutStates.TryGetValue(foldoutKey, out _))
                    _foldoutStates[foldoutKey] = false;

                Type concreteType = instance.GetType();
                string displayName = boundType == concreteType ? boundType.Name : $"{boundType.Name} ({concreteType.Name})";

                _foldoutStates[foldoutKey] = EditorGUILayout.Foldout(_foldoutStates[foldoutKey], displayName, true, _headerStyle);
                if (_foldoutStates[foldoutKey])
                {
                    EditorGUI.indentLevel++;
                    DrawSingletonFieldsAndProperties(instance);
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(5);
                }
            }

            // Stale foldout cleanup: only when context count changes
            CleanupStaleFoldoutStates(contextsSnapshot);

            EditorGUILayout.EndScrollView();
        }

        private void CleanupStaleFoldoutStates(List<IContext> contextsSnapshot)
        {
            if (_foldoutStates.Count <= 128) return;

            var validKeys = new HashSet<string>();
            for (int ci = 0; ci < contextsSnapshot.Count; ci++)
            {
                if (contextsSnapshot[ci] is Context ctx && ctx.Container != null)
                {
                    var registeredSingletons = ctx.Container.GetRegisteredSingletons();
                    if (registeredSingletons != null)
                    {
                        foreach (var kvp in registeredSingletons)
                        {
                            if (kvp.Value != null)
                                validKeys.Add($"{ci}_{kvp.Key.FullName}");
                        }
                    }
                }
            }

            var staleKeys = new List<string>();
            foreach (var key in _foldoutStates.Keys)
            {
                if (!validKeys.Contains(key))
                    staleKeys.Add(key);
            }

            foreach (var key in staleKeys)
                _foldoutStates.Remove(key);
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

            GUILayout.Label("Fields", _miniBoldLabelStyle);
            EditorGUI.indentLevel++;
            foreach (var field in fields)
            {
                if (field.Name.Contains("<") && field.Name.Contains(">"))
                    continue;
                DrawField(instance, field);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(3);

            GUILayout.Label("Properties", _miniBoldLabelStyle);
            EditorGUI.indentLevel++;
            foreach (var prop in properties)
            {
                DrawProperty(instance, prop);
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawField(object instance, FieldInfo field)
        {
            object value;
            try
            {
                value = field.GetValue(instance);
            }
            catch
            {
                EditorGUILayout.LabelField(field.Name, "<Unable to read field>");
                return;
            }

            EditorGUI.BeginChangeCheck();
            object newValue = DrawTypedField(field.Name, value, field.FieldType);
            if (EditorGUI.EndChangeCheck())
            {
                try
                {
                    if (instance is UnityEngine.Object unityObj)
                        Undo.RecordObject(unityObj, "Modify Model Field");
                    field.SetValue(instance, newValue);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private static void DrawProperty(object instance, PropertyInfo prop)
        {
            if (prop.GetIndexParameters().Length > 0) return;

            object value;
            try
            {
                value = prop.GetValue(instance);
            }
            catch
            {
                EditorGUILayout.LabelField(prop.Name, "<Unable to read property>");
                return;
            }

            if (prop.CanWrite && prop.GetSetMethod(true) != null)
            {
                EditorGUI.BeginChangeCheck();
                object newValue = DrawTypedField(prop.Name, value, prop.PropertyType);
                if (EditorGUI.EndChangeCheck())
                {
                    try
                    {
                        if (instance is UnityEngine.Object unityObj)
                            Undo.RecordObject(unityObj, "Modify Model Property");
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
                EditorGUILayout.LabelField(prop.Name, value != null ? value.ToString() : "null");
            }
        }

        private static object DrawTypedField(string label, object value, Type type)
        {
            if (type == typeof(int))
                return EditorGUILayout.IntField(label, (int)(value ?? 0));
            if (type == typeof(float))
                return EditorGUILayout.FloatField(label, (float)(value ?? 0f));
            if (type == typeof(double))
                return EditorGUILayout.DoubleField(label, (double)(value ?? 0.0));
            if (type == typeof(bool))
                return EditorGUILayout.Toggle(label, (bool)(value ?? false));
            if (type == typeof(string))
                return EditorGUILayout.TextField(label, (string)value ?? "");
            if (type == typeof(Vector2))
                return EditorGUILayout.Vector2Field(label, (Vector2)(value ?? Vector2.zero));
            if (type == typeof(Vector3))
                return EditorGUILayout.Vector3Field(label, (Vector3)(value ?? Vector3.zero));
            if (type == typeof(Color))
                return EditorGUILayout.ColorField(label, (Color)(value ?? Color.white));
            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(label, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));
            if (type == typeof(long))
                return EditorGUILayout.LongField(label, (long)(value ?? 0L));
            if (type == typeof(Vector4))
                return EditorGUILayout.Vector4Field(label, (Vector4)(value ?? Vector4.zero));
            if (type == typeof(Vector2Int))
                return EditorGUILayout.Vector2IntField(label, (Vector2Int)(value ?? Vector2Int.zero));
            if (type == typeof(Vector3Int))
                return EditorGUILayout.Vector3IntField(label, (Vector3Int)(value ?? Vector3Int.zero));
            if (type == typeof(Rect))
                return EditorGUILayout.RectField(label, (Rect)(value ?? Rect.zero));
            if (type == typeof(Bounds))
                return EditorGUILayout.BoundsField(label, (Bounds)(value ?? new Bounds()));

            EditorGUILayout.LabelField(label, value != null ? value.ToString() : "null");
            return value;
        }

        private void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, SeparatorColor);
        }
    }
}
