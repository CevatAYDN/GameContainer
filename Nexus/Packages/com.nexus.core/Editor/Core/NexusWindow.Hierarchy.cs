using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public partial class NexusWindow
    {
        // ==========================================
        // ── TAB 3: HIERARCHY & DATA
        // ==========================================
        private void BuildHierarchyTab()
        {
            var toolbar = NexusEditorStyles.CreateToolbar("HIERARCHY GRAPH & DI DATA INSPECTOR");
            _contentArea.Add(toolbar);

            // Split View layout
            var splitView = new VisualElement();
            splitView.style.flexDirection = FlexDirection.Row;
            splitView.style.flexGrow = 1;

            // Left Panel: Context Tree Graph
            var leftPanel = new ScrollView();
            leftPanel.style.width = new Length(50, LengthUnit.Percent);
            leftPanel.style.paddingLeft = 12;
            leftPanel.style.paddingRight = 12;
            leftPanel.style.paddingTop = 10;
            leftPanel.style.paddingBottom = 10;
            leftPanel.style.borderRightWidth = 1;
            leftPanel.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            splitView.Add(leftPanel);

            // Right Panel: Data Inspector Detail Drawer
            var rightPanel = new VisualElement();
            rightPanel.style.width = new Length(50, LengthUnit.Percent);
            rightPanel.style.paddingLeft = 12;
            rightPanel.style.paddingRight = 12;
            rightPanel.style.paddingTop = 10;
            rightPanel.style.paddingBottom = 10;

            var detailTitle = new Label("DI CONTAINER INSPECTOR");
            detailTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            detailTitle.style.fontSize = 12;
            detailTitle.style.color = new StyleColor(Color.gray);
            detailTitle.style.marginBottom = 10;
            rightPanel.Add(detailTitle);

            var inspectorContainer = new IMGUIContainer(DrawDataInspectorIMGUI);
            inspectorContainer.style.flexGrow = 1;
            rightPanel.Add(inspectorContainer);
            splitView.Add(rightPanel);

            _contentArea.Add(splitView);

            // Rebuild context graph on left panel
            var activeContexts = NexusRuntime.ActiveContexts;
            if (activeContexts == null || activeContexts.Count == 0)
            {
                NexusEditorStyles.CreateInfoCard(leftPanel, "NEXUS CONTEXT GRAPH \u2014 OFFLINE", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBg,
                    "No active Nexus Contexts found. Enter <b>Play Mode</b> to inspect context hierarchy, parent-child relationships, and resolved DI singletons.\n\n" +
                    "Each active Context will appear as a card showing its ScopeTag, handler count, and resolved singletons.");

                int sceneRootCount = CountSceneRoots();
                if (sceneRootCount > 0)
                {
                    NexusEditorStyles.CreateInfoCard(leftPanel, $"SCENE ROOTS DETECTED ({sceneRootCount})", NexusEditorStyles.AccentYellow, NexusEditorStyles.CardBgYellow,
                        $"Found {sceneRootCount} Root GameObject(s) in the scene. These will initialize Contexts in Play Mode.");
                }
                return;
            }

            // Build hierarchy dictionary
            var rootContexts = new List<Context>();
            var childMap = new Dictionary<Context, List<Context>>();

            for (int i = 0; i < activeContexts.Count; i++)
            {
                if (activeContexts[i] is Context ctx)
                {
                    if (ctx.Parent == null)
                        rootContexts.Add(ctx);
                    else if (ctx.Parent is Context parentCtx)
                    {
                        if (!childMap.ContainsKey(parentCtx))
                            childMap[parentCtx] = new List<Context>();
                        childMap[parentCtx].Add(ctx);
                    }
                }
            }

            foreach (var rootCtx in rootContexts)
            {
                var card = RenderContextCard(rootCtx, childMap, 0);
                leftPanel.Add(card);
            }
        }

        private VisualElement RenderContextCard(Context ctx, Dictionary<Context, List<Context>> childMap, int depth)
        {
            var card = new VisualElement();
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            var borderColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            card.style.borderTopColor = borderColor;
            card.style.borderBottomColor = borderColor;
            card.style.borderLeftColor = borderColor;
            card.style.borderRightColor = borderColor;
            card.style.borderTopLeftRadius = 5;
            card.style.borderTopRightRadius = 5;
            card.style.borderBottomLeftRadius = 5;
            card.style.borderBottomRightRadius = 5;
            
            // Highlight if inspected
            bool isInspected = _selectedContextForInspector == ctx;
            card.style.backgroundColor = isInspected ? new StyleColor(new Color(0.18f, 0.22f, 0.26f)) : new StyleColor(new Color(0.15f, 0.15f, 0.17f));
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.marginTop = 6;
            card.style.marginBottom = 6;

            // Header Row
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;

            var title = new Label(ctx.ScopeTag ?? "Context");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 11;
            title.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
            header.Add(title);

            // Add click detector to select context
            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                _selectedContextForInspector = ctx;
                RefreshActiveTabContent();
                evt.StopPropagation();
            });

            int handlerCount = 0;
            if (ctx.SignalBusInternal?.CommandHandlers != null)
            {
                foreach (var kvp in ctx.SignalBusInternal.CommandHandlers)
                {
                    if (kvp.Value != null) handlerCount += kvp.Value.Count;
                }
            }

            var pill = new Label($"{handlerCount} Handlers")
            {
                style = {
                    fontSize = 8,
                    backgroundColor = new StyleColor(new Color(0.2f, 0.35f, 0.2f)),
                    color = new StyleColor(new Color(0.6f, 1f, 0.6f)),
                    paddingLeft = 3,
                    paddingRight = 3,
                    paddingTop = 1,
                    paddingBottom = 1,
                    marginLeft = 6,
                    borderTopLeftRadius = 2,
                    borderTopRightRadius = 2,
                    borderBottomLeftRadius = 2,
                    borderBottomRightRadius = 2,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            header.Add(pill);

            // Ping SO Config button
            if (ctx.ContextData != null)
            {
                var pingBtn = new Button(() =>
                {
                    Selection.activeObject = ctx.ContextData;
                    EditorGUIUtility.PingObject(ctx.ContextData);
                }) { text = "Config SO ↗" };
                pingBtn.style.fontSize = 8;
                pingBtn.style.backgroundColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
                pingBtn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                pingBtn.style.marginLeft = StyleKeyword.Auto;
                pingBtn.style.paddingLeft = 4;
                pingBtn.style.paddingRight = 4;
                header.Add(pingBtn);
            }

            card.Add(header);

            // Nested active singletons list overview
            var singletonsList = new VisualElement();
            singletonsList.style.marginTop = 6;
            var activeSingletons = ctx.Container.GetActiveSingletons();
            int singletonCount = 0;

            foreach (var instance in activeSingletons)
            {
                if (instance == null || instance is NexusDI || instance is IContext || instance is ISignalBus) continue;
                var type = instance.GetType();
                var item = new Label($"• {type.Name}") { style = { fontSize = 9, color = Color.white } };
                singletonsList.Add(item);
                singletonCount++;
            }

            if (singletonCount == 0)
            {
                singletonsList.Add(new Label("  None resolved.") { style = { fontSize = 9, color = Color.gray } });
            }

            card.Add(singletonsList);

            // Recursively draw child contexts
            if (childMap.TryGetValue(ctx, out var children) && children.Count > 0)
            {
                var childrenContainer = new VisualElement();
                childrenContainer.style.marginTop = 8;
                childrenContainer.style.paddingLeft = 10;
                childrenContainer.style.borderLeftWidth = 1;
                childrenContainer.style.borderLeftColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));

                foreach (var child in children)
                {
                    childrenContainer.Add(RenderContextCard(child, childMap, depth + 1));
                }
                card.Add(childrenContainer);
            }

            return card;
        }

        private void DrawDataInspectorIMGUI()
        {
            EnsureStyles();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to inspect DI Container details.", MessageType.Info);
                return;
            }

            if (_selectedContextForInspector == null)
            {
                EditorGUILayout.HelpBox("Select a Context card on the left panel to inspect its resolved dependencies.", MessageType.Info);
                return;
            }

            var singletons = _selectedContextForInspector.Container.GetRegisteredSingletons();
            if (singletons == null || singletons.Count == 0)
            {
                EditorGUILayout.HelpBox("No resolved singletons or models found in this context's container.", MessageType.Info);
                return;
            }

            // Search filter
            _inspectorSearchFilter = EditorGUILayout.TextField("Search Filter", _inspectorSearchFilter);
            EditorGUILayout.Space(5);

            _inspectorScrollPosition = EditorGUILayout.BeginScrollView(_inspectorScrollPosition);

            bool hasFilter = !string.IsNullOrWhiteSpace(_inspectorSearchFilter);
            foreach (var kvp in singletons)
            {
                Type boundType = kvp.Key;
                object instance = kvp.Value;
                if (instance == null) continue;

                if (boundType == typeof(NexusDI) || boundType == typeof(Context) || boundType == typeof(IContext) ||
                    boundType == typeof(SignalBus) || boundType == typeof(ISignalBus) || boundType == typeof(HybridQueue) ||
                    boundType == typeof(CommandPoolManager) || boundType == typeof(ViewBinder))
                    continue;

                // Filter check
                if (hasFilter)
                {
                    string name = boundType.Name;
                    string fullName = boundType.FullName;
                    if (name.IndexOf(_inspectorSearchFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        fullName.IndexOf(_inspectorSearchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                string foldoutKey = $"{_selectedContextForInspector.ScopeTag}_{boundType.FullName}";
                if (!_inspectorFoldoutStates.TryGetValue(foldoutKey, out _))
                    _inspectorFoldoutStates[foldoutKey] = false;

                Type concreteType = instance.GetType();
                string displayName = boundType == concreteType ? boundType.Name : $"{boundType.Name} ({concreteType.Name})";

                _inspectorFoldoutStates[foldoutKey] = EditorGUILayout.Foldout(_inspectorFoldoutStates[foldoutKey], displayName, true);
                if (_inspectorFoldoutStates[foldoutKey])
                {
                    EditorGUI.indentLevel++;
                    DrawSingletonFieldsAndProperties(instance);
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(5);
                }
            }

            // Throttled foldout cleanup
            if (EditorApplication.timeSinceStartup - _lastInspectorCleanupTime > 2.0)
            {
                _lastInspectorCleanupTime = EditorApplication.timeSinceStartup;
                CleanupStaleFoldoutStates();
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

        private void CleanupStaleFoldoutStates()
        {
            if (_inspectorFoldoutStates.Count <= 128) return;

            var validKeys = new HashSet<string>();
            var contextsSnapshot = NexusRuntime.ActiveContexts;
            if (contextsSnapshot == null) return;

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
                                validKeys.Add($"{ctx.ScopeTag}_{kvp.Key.FullName}");
                        }
                    }
                }
            }

            var staleKeys = new List<string>();
            foreach (var key in _inspectorFoldoutStates.Keys)
            {
                if (!validKeys.Contains(key))
                    staleKeys.Add(key);
            }

            foreach (var key in staleKeys)
                _inspectorFoldoutStates.Remove(key);
        }

        private static void DrawField(object instance, FieldInfo field)
        {
            object value;
            try { value = field.GetValue(instance); }
            catch { EditorGUILayout.LabelField(field.Name, "<Unable to read>"); return; }

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
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private static void DrawProperty(object instance, PropertyInfo prop)
        {
            if (prop.GetIndexParameters().Length > 0) return;

            object value;
            try { value = prop.GetValue(instance); }
            catch { EditorGUILayout.LabelField(prop.Name, "<Unable to read>"); return; }

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
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            else
            {
                EditorGUILayout.LabelField(prop.Name, value != null ? value.ToString() : "null");
            }
        }

        // ==========================================
        // ── CORE DATA REFLECTION UTILITIES
        // ==========================================
        private static object DrawTypedField(string label, object value, Type type)
        {
            if (type == typeof(int)) return EditorGUILayout.IntField(label, (int)(value ?? 0));
            if (type == typeof(float)) return EditorGUILayout.FloatField(label, (float)(value ?? 0f));
            if (type == typeof(double)) return EditorGUILayout.DoubleField(label, (double)(value ?? 0.0));
            if (type == typeof(bool)) return EditorGUILayout.Toggle(label, (bool)(value ?? false));
            if (type == typeof(string)) return EditorGUILayout.TextField(label, (string)value ?? "");
            if (type == typeof(long)) return EditorGUILayout.LongField(label, (long)(value ?? 0L));
            if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(label, (Vector2)(value ?? Vector2.zero));
            if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, (Vector3)(value ?? Vector3.zero));
            if (type == typeof(Vector4)) return EditorGUILayout.Vector4Field(label, (Vector4)(value ?? Vector4.zero));
            if (type == typeof(Color)) return EditorGUILayout.ColorField(label, (Color)(value ?? Color.white));
            if (type == typeof(Vector2Int)) return EditorGUILayout.Vector2IntField(label, (Vector2Int)(value ?? Vector2Int.zero));
            if (type == typeof(Vector3Int)) return EditorGUILayout.Vector3IntField(label, (Vector3Int)(value ?? Vector3Int.zero));
            if (type == typeof(Rect)) return EditorGUILayout.RectField(label, (Rect)(value ?? Rect.zero));
            if (type == typeof(Bounds)) return EditorGUILayout.BoundsField(label, (Bounds)(value ?? new Bounds()));
            if (type.IsEnum) return EditorGUILayout.EnumPopup(label, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));

            EditorGUILayout.LabelField(label, value != null ? value.ToString() : "null");
            return value;
        }

        private static int ComputeContextVersion(IReadOnlyList<IContext> contexts)
        {
            if (contexts == null) return 0;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < contexts.Count; i++)
                {
                    if (contexts[i] is Context ctx)
                    {
                        hash = hash * 31 + (ctx.ScopeTag?.GetHashCode() ?? 0);
                        hash = hash * 31 + (ctx.Parent?.GetHashCode() ?? 0);
                        if (ctx.Container != null)
                        {
                            hash = hash * 31 + ctx.Container.ActiveSingletonsCount;
                        }
                    }
                }
                return hash;
            }
        }
    }
}
