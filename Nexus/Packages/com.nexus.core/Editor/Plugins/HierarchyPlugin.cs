using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class HierarchyPlugin : NexusEditorPlugin
    {
        public override string Id => "Hierarchy";
        public override string DisplayName => "Hierarchy & Data";
        public override int Order => 2;

        private VisualElement _view;
        private VisualElement _leftPanel;
        private VisualElement _rightPanel;
        private ScrollView _inspectorScroll;
        
        private Context _selectedContext;
        private string _searchFilter = "";
        private readonly Dictionary<string, FoldoutState> _foldoutCache = new();
        private readonly List<BindingTracker> _bindingTrackers = new();

        private class FoldoutState
        {
            public bool Expanded;
            public VisualElement ContentContainer;
        }

        private class BindingTracker
        {
            public object Instance;
            public MemberInfo Member;
            public BindableElement UIElement;
            public Type MemberType;
            public object LastValue;
            public float FlashTimeRemaining;
        }

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("hierarchy_title"));
            _view.Add(toolbar);

            var splitView = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };

            // Left Panel (Tree Graph)
            _leftPanel = new ScrollView { style = { width = new Length(50, LengthUnit.Percent), paddingLeft = 12, paddingRight = 12, paddingTop = 10, paddingBottom = 10 } };
            _leftPanel.style.borderRightWidth = 1;
            _leftPanel.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            splitView.Add(_leftPanel);

            // Right Panel (DI Inspector)
            _rightPanel = new VisualElement { style = { width = new Length(50, LengthUnit.Percent), paddingLeft = 12, paddingRight = 12, paddingTop = 10, paddingBottom = 10 } };
            
            var detailTitle = new Label(NexusLang.Get("hierarchy_di_inspector")) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, color = new StyleColor(Color.gray), marginBottom = 10 } };
            _rightPanel.Add(detailTitle);

            _inspectorScroll = new ScrollView { style = { flexGrow = 1 } };
            _rightPanel.Add(_inspectorScroll);

            splitView.Add(_rightPanel);
            _view.Add(splitView);

            // Bind to runtime context events
            NexusRuntime.OnContextRegistered += OnContextsChanged;
            NexusRuntime.OnContextUnregistered += OnContextsChanged;

            RebuildContextTree();
            RebuildInspector();

            return _view;
        }

        public override void OnDisable()
        {
            NexusRuntime.OnContextRegistered -= OnContextsChanged;
            NexusRuntime.OnContextUnregistered -= OnContextsChanged;
            _bindingTrackers.Clear();
        }

        private void OnContextsChanged(IContext ctx)
        {
            RebuildContextTree();
            if (_selectedContext == ctx)
            {
                _selectedContext = null;
                RebuildInspector();
            }
        }

        private void RebuildContextTree()
        {
            if (_leftPanel == null) return;
            _leftPanel.Clear();

            var activeContexts = NexusRuntime.ActiveContexts;
            if (activeContexts == null || activeContexts.Count == 0)
            {
                NexusEditorStyles.CreateInfoCard(_leftPanel, "NEXUS CONTEXT GRAPH — OFFLINE", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBg,
                    "No active Nexus Contexts found. Enter Play Mode to inspect context hierarchy, parent-child relationships, and resolved DI singletons.");

                int sceneRootCount = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude).Length;
                if (sceneRootCount > 0)
                {
                    NexusEditorStyles.CreateInfoCard(_leftPanel, $"SCENE ROOTS DETECTED ({sceneRootCount})", NexusEditorStyles.AccentYellow, NexusEditorStyles.CardBgYellow,
                        $"Found {sceneRootCount} Root GameObject(s) in the scene. These will initialize Contexts in Play Mode.");
                }
                return;
            }

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
                        if (!childMap.TryGetValue(parentCtx, out var children))
                        {
                            children = new List<Context>();
                            childMap[parentCtx] = children;
                        }
                        children.Add(ctx);
                    }
                }
            }

            foreach (var rootCtx in rootContexts)
            {
                _leftPanel.Add(RenderContextCard(rootCtx, childMap));
            }
        }

        private VisualElement RenderContextCard(Context ctx, Dictionary<Context, List<Context>> childMap)
        {
            var card = new VisualElement();
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            var borderColor = new StyleColor(NexusEditorStyles.BorderLight);
            card.style.borderTopColor = borderColor;
            card.style.borderBottomColor = borderColor;
            card.style.borderLeftColor = borderColor;
            card.style.borderRightColor = borderColor;
            card.style.borderTopLeftRadius = 5;
            card.style.borderTopRightRadius = 5;
            card.style.borderBottomLeftRadius = 5;
            card.style.borderBottomRightRadius = 5;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.marginTop = 6;
            card.style.marginBottom = 6;

            bool isInspected = _selectedContext == ctx;
            card.style.backgroundColor = isInspected ? new StyleColor(NexusEditorStyles.HighlightBg) : new StyleColor(NexusEditorStyles.RowAlt);

            // Mouse down selection callback
            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                _selectedContext = ctx;
                RebuildContextTree();
                RebuildInspector();
                evt.StopPropagation();
            });

            // Header Row
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            
            var title = new Label(ctx.ScopeTag ?? "Context") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, color = new StyleColor(NexusEditorStyles.AccentBlue) } };
            header.Add(title);

            int handlerCount = 0;
            if (ctx.SignalBusInternal?.CommandHandlers != null)
            {
                foreach (var kvp in ctx.SignalBusInternal.CommandHandlers)
                {
                    if (kvp.Value != null) handlerCount += kvp.Value.Count;
                }
            }

            var pill = NexusEditorStyles.CreatePill($"{handlerCount} Handlers", NexusEditorStyles.CardBgGreen, NexusEditorStyles.AccentGreenText);
            header.Add(pill);

            if (ctx.ContextData != null)
            {
                var pingBtn = new Button(() =>
                {
                    Selection.activeObject = ctx.ContextData;
                    EditorGUIUtility.PingObject(ctx.ContextData);
                }) { text = "Config SO" };
                pingBtn.style.fontSize = 8;
                pingBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnGray);
                pingBtn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                pingBtn.style.marginLeft = StyleKeyword.Auto;
                pingBtn.style.paddingLeft = 4;
                pingBtn.style.paddingRight = 4;
                header.Add(pingBtn);
            }

            card.Add(header);

            // Nested active singletons list
            var singletonsList = new VisualElement { style = { marginTop = 6 } };
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
                singletonsList.Add(new Label(NexusLang.Get("hierarchy_none_resolved")) { style = { fontSize = 9, color = Color.gray } });
            }
            card.Add(singletonsList);

            // Recursively draw child contexts
            if (childMap.TryGetValue(ctx, out var children) && children.Count > 0)
            {
                var childrenContainer = new VisualElement { style = { marginTop = 8, paddingLeft = 10, borderLeftWidth = 1, borderLeftColor = new StyleColor(NexusEditorStyles.BorderLight) } };
                foreach (var child in children)
                {
                    childrenContainer.Add(RenderContextCard(child, childMap));
                }
                card.Add(childrenContainer);
            }

            return card;
        }

        private void RebuildInspector()
        {
            if (_inspectorScroll == null) return;
            _inspectorScroll.Clear();
            _bindingTrackers.Clear();

            if (!Application.isPlaying)
            {
                _inspectorScroll.Add(NexusEditorStyles.CreateEmptyState("Enter Play Mode to inspect DI Container details."));
                return;
            }

            if (_selectedContext == null)
            {
                _inspectorScroll.Add(NexusEditorStyles.CreateEmptyState("Select a Context card on the left panel to inspect its resolved dependencies."));
                return;
            }

            var singletons = _selectedContext.Container.GetRegisteredSingletons();
            if (singletons == null || singletons.Count == 0)
            {
                _inspectorScroll.Add(NexusEditorStyles.CreateEmptyState("No resolved singletons or models found in this context's container."));
                return;
            }

            // Search Bar
            var searchField = new TextField("Search Filter") { value = _searchFilter };
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue;
                FilterInspectorList();
            });
            _inspectorScroll.Add(searchField);

            var listContainer = new VisualElement { name = "InspectorList", style = { marginTop = 10 } };
            _inspectorScroll.Add(listContainer);

            bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);

            foreach (var kvp in singletons)
            {
                Type boundType = kvp.Key;
                object instance = kvp.Value;
                if (instance == null) continue;

                // Filter internal Nexus types
                if (boundType == typeof(NexusDI) || boundType == typeof(Context) || boundType == typeof(IContext) ||
                    boundType == typeof(SignalBus) || boundType == typeof(ISignalBus) || boundType == typeof(HybridQueue) ||
                    boundType == typeof(CommandPoolManager) || boundType == typeof(ViewBinder))
                    continue;

                Type concreteType = instance.GetType();
                string displayName = boundType == concreteType ? boundType.Name : $"{boundType.Name} ({concreteType.Name})";

                string foldoutKey = $"{_selectedContext.ScopeTag}_{boundType.FullName}";
                if (!_foldoutCache.TryGetValue(foldoutKey, out var foldoutState))
                {
                    foldoutState = new FoldoutState { Expanded = false };
                    _foldoutCache[foldoutKey] = foldoutState;
                }

                var foldout = new Foldout
                {
                    text = displayName,
                    value = foldoutState.Expanded,
                    name = foldoutKey
                };
                foldout.style.marginTop = 3;

                var foldoutContent = new VisualElement { style = { paddingLeft = 15, paddingBottom = 5 } };
                foldoutState.ContentContainer = foldoutContent;
                foldout.Add(foldoutContent);

                foldout.RegisterValueChangedCallback(evt =>
                {
                    if (evt.target == foldout)
                    {
                        foldoutState.Expanded = evt.newValue;
                        if (evt.newValue)
                        {
                            BuildInstanceInspector(foldoutContent, instance);
                        }
                        else
                        {
                            foldoutContent.Clear();
                            _bindingTrackers.RemoveAll(t => t.Instance == instance);
                        }
                    }
                });

                if (foldoutState.Expanded)
                {
                    BuildInstanceInspector(foldoutContent, instance);
                }

                listContainer.Add(foldout);
            }

            FilterInspectorList();
        }

        private void FilterInspectorList()
        {
            if (_inspectorScroll == null) return;
            var list = _inspectorScroll.Q<VisualElement>("InspectorList");
            if (list == null) return;

            bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);

            foreach (var child in list.Children())
            {
                if (child is Foldout foldout)
                {
                    if (hasFilter)
                    {
                        bool matches = foldout.text.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                        foldout.style.display = matches ? DisplayStyle.Flex : DisplayStyle.None;
                    }
                    else
                    {
                        foldout.style.display = DisplayStyle.Flex;
                    }
                }
            }
        }

        private void BuildInstanceInspector(VisualElement container, object instance)
        {
            container.Clear();
            _bindingTrackers.RemoveAll(t => t.Instance == instance);

            Type type = instance.GetType();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (fields.Length == 0 && properties.Length == 0)
            {
                container.Add(new Label(NexusLang.Get("hierarchy_no_fields")) { style = { color = Color.gray, fontSize = 9, unityFontStyleAndWeight = FontStyle.Italic } });
                return;
            }

            // Fields section
                            var fieldsHeader = new Label(NexusLang.Get("hierarchy_fields")) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginTop = 4 } };
            container.Add(fieldsHeader);

            foreach (var field in fields)
            {
                if (field.Name.Contains("<") && field.Name.Contains(">")) // Skip auto-property backing fields
                    continue;

                var row = CreateFieldUI(instance, field, field.FieldType, () => field.GetValue(instance), val => field.SetValue(instance, val));
                if (row != null) container.Add(row);
            }

            // Properties section
                            var propsHeader = new Label(NexusLang.Get("hierarchy_properties")) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginTop = 8 } };
            container.Add(propsHeader);

            foreach (var prop in properties)
            {
                if (prop.GetIndexParameters().Length > 0) continue; // Skip indexers

                var row = CreateFieldUI(instance, prop, prop.PropertyType,
                    () => prop.GetValue(instance),
                    prop.CanWrite && prop.GetSetMethod(true) != null ? val => prop.SetValue(instance, val) : (Action<object>)null);
                if (row != null) container.Add(row);
            }
        }

        private VisualElement CreateFieldUI(object instance, MemberInfo member, Type type, Func<object> getter, Action<object> setter)
        {
            VisualElement element = null;
            object initialValue = null;
            try { initialValue = getter(); } catch { }

            bool isReadOnly = setter == null;

            if (type == typeof(int))
            {
                var field = new IntegerField(member.Name) { value = (int)(initialValue ?? 0) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(float))
            {
                var field = new FloatField(member.Name) { value = (float)(initialValue ?? 0f) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(double))
            {
                var field = new DoubleField(member.Name) { value = (double)(initialValue ?? 0.0) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(bool))
            {
                var field = new Toggle(member.Name) { value = (bool)(initialValue ?? false) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(string))
            {
                var field = new TextField(member.Name) { value = (string)initialValue ?? "" };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(Vector2))
            {
                var field = new Vector2Field(member.Name) { value = (Vector2)(initialValue ?? Vector2.zero) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(Vector3))
            {
                var field = new Vector3Field(member.Name) { value = (Vector3)(initialValue ?? Vector3.zero) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type == typeof(Color))
            {
                var field = new ColorField(member.Name) { value = (Color)(initialValue ?? Color.white) };
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else if (type.IsEnum)
            {
                var field = new EnumField(member.Name, (Enum)(initialValue ?? Enum.GetValues(type).GetValue(0)));
                if (!isReadOnly) field.RegisterValueChangedCallback(evt => { UndoRecord(instance); setter(evt.newValue); });
                else field.SetEnabled(false);
                element = field;
            }
            else
            {
                // Fallback for custom objects / classes
                var label = new Label($"{member.Name}: {initialValue ?? "null"}") { style = { color = Color.white, fontSize = 10 } };
                return label;
            }

            if (element is BindableElement bindable)
            {
                _bindingTrackers.Add(new BindingTracker
                {
                    Instance = instance,
                    Member = member,
                    UIElement = bindable,
                    MemberType = type,
                    LastValue = initialValue,
                    FlashTimeRemaining = 0f
                });
            }

            return element;
        }

        private void UndoRecord(object instance)
        {
            if (instance is UnityEngine.Object unityObj)
            {
                Undo.RecordObject(unityObj, "Modify Model Member");
            }
        }

        // Keep values in expanded foldouts updated (called dynamically by Scheduler in NexusWindow)
        public void UpdateVisibleTrackers()
        {
            if (!Application.isPlaying || _bindingTrackers.Count == 0) return;

            foreach (var tracker in _bindingTrackers)
            {
                try
                {
                    object currentVal = null;
                    if (tracker.Member is FieldInfo field) currentVal = field.GetValue(tracker.Instance);
                    else if (tracker.Member is PropertyInfo prop) currentVal = prop.GetValue(tracker.Instance);

                    bool valueChanged = false;
                    if (tracker.LastValue == null && currentVal != null) valueChanged = true;
                    else if (tracker.LastValue != null && !tracker.LastValue.Equals(currentVal)) valueChanged = true;

                    if (valueChanged)
                    {
                        tracker.LastValue = currentVal;
                        tracker.FlashTimeRemaining = 0.6f;
                        tracker.UIElement.style.backgroundColor = new StyleColor(new Color(0.18f, 0.45f, 0.18f, 0.7f));
                    }
                    else if (tracker.FlashTimeRemaining > 0f)
                    {
                        tracker.FlashTimeRemaining -= 0.2f;
                        if (tracker.FlashTimeRemaining <= 0f)
                        {
                            tracker.UIElement.style.backgroundColor = new StyleColor(StyleKeyword.Null);
                        }
                    }

                    if (tracker.MemberType == typeof(int) && tracker.UIElement is IntegerField intField)
                    {
                        int val = (int)(currentVal ?? 0);
                        if (intField.value != val) intField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(float) && tracker.UIElement is FloatField floatField)
                    {
                        float val = (float)(currentVal ?? 0f);
                        if (floatField.value != val) floatField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(double) && tracker.UIElement is DoubleField doubleField)
                    {
                        double val = (double)(currentVal ?? 0.0);
                        if (doubleField.value != val) doubleField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(bool) && tracker.UIElement is Toggle toggleField)
                    {
                        bool val = (bool)(currentVal ?? false);
                        if (toggleField.value != val) toggleField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(string) && tracker.UIElement is TextField textField)
                    {
                        string val = (string)currentVal ?? "";
                        if (textField.value != val) textField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(Vector2) && tracker.UIElement is Vector2Field vec2Field)
                    {
                        Vector2 val = (Vector2)(currentVal ?? Vector2.zero);
                        if (vec2Field.value != val) vec2Field.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(Vector3) && tracker.UIElement is Vector3Field vec3Field)
                    {
                        Vector3 val = (Vector3)(currentVal ?? Vector3.zero);
                        if (vec3Field.value != val) vec3Field.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType == typeof(Color) && tracker.UIElement is ColorField colorField)
                    {
                        Color val = (Color)(currentVal ?? Color.white);
                        if (colorField.value != val) colorField.SetValueWithoutNotify(val);
                    }
                    else if (tracker.MemberType.IsEnum && tracker.UIElement is EnumField enumField)
                    {
                        Enum val = (Enum)(currentVal ?? Enum.GetValues(tracker.MemberType).GetValue(0));
                        if (!Equals(enumField.value, val)) enumField.SetValueWithoutNotify(val);
                    }
                }
                catch { }
            }
        }
    }
}
