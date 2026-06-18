using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    internal class NexusSignalTesterWindow : EditorWindow
    {
        private Vector2 _scrollPos;
        private int _selectedSignalIndex;
        private Type _selectedSignalType;
        private object _signalInstance;
        private FieldInfo[] _signalFields;
        private string _resultLog;
        private Color _resultColor = Color.white;

        [MenuItem("Window/Nexus/Signal Tester")]
        internal static void ShowWindow()
        {
            var window = GetWindow<NexusSignalTesterWindow>("Nexus Signal Tester");
            window.minSize = new Vector2(380, 400);
            window.Show();
        }

        private void OnEnable()
        {
            _signalInstance = null;
            _selectedSignalType = null;
            _resultLog = null;
        }

        private void OnGUI()
        {
            var signalTypes = GatherSignalTypes();
            if (signalTypes == null || signalTypes.Count == 0)
            {
                EditorGUILayout.HelpBox("No signal types found. Create a Root with SignalHandler attributes to register signals.", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(5);
            NexusGUIHelper.TitleLabel("SIGNAL TESTER");

            string[] signalNames = signalTypes.Select(t => t.Name).ToArray();
            EditorGUI.BeginChangeCheck();
            _selectedSignalIndex = EditorGUILayout.Popup("Signal Type", _selectedSignalIndex, signalNames);
            if (EditorGUI.EndChangeCheck())
            {
                _selectedSignalType = signalTypes[_selectedSignalIndex];
                RebuildSignalInstance();
                _resultLog = null;
            }

            if (_selectedSignalType != null && _signalInstance != null)
            {
                EditorGUILayout.Space(8);
                NexusGUIHelper.Separator();
                EditorGUILayout.LabelField("Signal Fields", EditorStyles.boldLabel);

                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

                for (int i = 0; i < _signalFields.Length; i++)
                {
                    var field = _signalFields[i];
                    object fieldValue = field.GetValue(_signalInstance);
                    object newValue = DrawTypedField(field.Name, fieldValue, field.FieldType);
                    if (!Equals(fieldValue, newValue))
                    {
                        field.SetValue(_signalInstance, newValue);
                    }
                }

                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(10);
                NexusGUIHelper.Separator();

                if (GUILayout.Button("Fire Signal", GUILayout.Height(30)))
                {
                    FireSelectedSignal();
                }

                if (!string.IsNullOrEmpty(_resultLog))
                {
                    EditorGUILayout.Space(8);
                    var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
                    style.normal.textColor = _resultColor;
                    EditorGUILayout.LabelField(_resultLog, style);
                }
            }
        }

        private void RebuildSignalInstance()
        {
            if (_selectedSignalType == null) return;
            try
            {
                _signalInstance = Activator.CreateInstance(_selectedSignalType);
                _signalFields = _selectedSignalType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                _signalInstance = null;
                _signalFields = Array.Empty<FieldInfo>();
                _resultLog = $"Failed to create signal: {ex.Message}";
                _resultColor = Color.red;
            }
        }

        private void FireSelectedSignal()
        {
            if (_selectedSignalType == null || _signalInstance == null)
            {
                _resultLog = "No signal type selected.";
                _resultColor = Color.yellow;
                return;
            }

            try
            {
                var signalBus = FindActiveSignalBus();
                if (signalBus == null)
                {
                    _resultLog = "No active SignalBus found. Enter Play Mode first.";
                    _resultColor = Color.yellow;
                    return;
                }

                var fireGeneric = typeof(SignalBus).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Fire" && m.IsGenericMethodDefinition);
                var fireMethod = fireGeneric.MakeGenericMethod(_selectedSignalType);

                fireMethod.Invoke(signalBus, new[] { _signalInstance });

                _resultLog = $"\u2713 Fired: {_selectedSignalType.Name}";
                _resultColor = NexusGUIHelper.GreenColor;
            }
            catch (Exception ex)
            {
                _resultLog = $"Fire failed: {ex.Message}";
                _resultColor = Color.red;
                Debug.LogException(ex);
            }
        }

        private static SignalBus FindActiveSignalBus()
        {
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null) return null;
            for (int i = 0; i < contexts.Count; i++)
            {
                if (contexts[i] is Context ctx && ctx.SignalBus != null)
                    return ctx.SignalBus as SignalBus;
            }
            return null;
        }

        private static List<Type> GatherSignalTypes()
        {
            var seen = new HashSet<string>();
            var types = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") || name.StartsWith("Unity") || name.StartsWith("mscorlib"))
                    continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;
                        foreach (var attr in type.GetCustomAttributes<SignalHandlerAttribute>())
                        {
                            var sigType = attr.SignalType;
                            if (sigType != null && !seen.Contains(sigType.FullName))
                            {
                                seen.Add(sigType.FullName);
                                types.Add(sigType);
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return types;
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
            if (type == typeof(long))
                return EditorGUILayout.LongField(label, (long)(value ?? 0L));
            if (type == typeof(Vector2))
                return EditorGUILayout.Vector2Field(label, (Vector2)(value ?? Vector2.zero));
            if (type == typeof(Vector3))
                return EditorGUILayout.Vector3Field(label, (Vector3)(value ?? Vector3.zero));
            if (type == typeof(Vector4))
                return EditorGUILayout.Vector4Field(label, (Vector4)(value ?? Vector4.zero));
            if (type == typeof(Color))
                return EditorGUILayout.ColorField(label, (Color)(value ?? Color.white));
            if (type == typeof(Vector2Int))
                return EditorGUILayout.Vector2IntField(label, (Vector2Int)(value ?? Vector2Int.zero));
            if (type == typeof(Vector3Int))
                return EditorGUILayout.Vector3IntField(label, (Vector3Int)(value ?? Vector3Int.zero));
            if (type == typeof(Rect))
                return EditorGUILayout.RectField(label, (Rect)(value ?? Rect.zero));
            if (type.IsEnum)
                return EditorGUILayout.EnumPopup(label, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));

            EditorGUILayout.LabelField(label, value != null ? value.ToString() : "null");
            return value;
        }
    }

    internal static class NexusGUIHelper
    {
        internal static readonly Color GreenColor = new(0.4f, 1f, 0.4f);

        private static GUIStyle _titleStyle;
        private static GUIStyle _separatorStyle;

        internal static void TitleLabel(string text)
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, margin = new RectOffset(5, 5, 5, 5) };
                _titleStyle.normal.textColor = new Color(0.3f, 0.8f, 1f);
            }
            EditorGUILayout.LabelField(text, _titleStyle);
        }

        internal static void Separator()
        {
            if (_separatorStyle == null)
            {
                _separatorStyle = new GUIStyle(GUI.skin.box) { margin = new RectOffset(0, 0, 4, 4), border = new RectOffset(1, 1, 1, 1) };
                _separatorStyle.normal.background = Texture2D.grayTexture;
            }
            var rect = EditorGUILayout.GetControlRect(false, 2);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
        }
    }
}
