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
    public class ExplorerPlugin : NexusEditorPlugin
    {
        public override string Id => "Explorer";
        public override string DisplayName => "Signal Explorer";
        public override int Order => 3;

        private VisualElement _view;
        private ScrollView _explorerScrollView;
        private DropdownField _assemblyDropdown;
        private TextField _searchField;

        private VisualElement _testerPanel;
        private ScrollView _testerFormContainer;
        private DropdownField _contextTargetDropdown;
        private Button _fireButton;
        private Label _resultLogLabel;

        private string _searchQuery = "";
        private string _selectedAssembly = "All Assemblies";
        private List<MappingInfo> _allMappings = new();
        private readonly List<VisualElement> _renderedRows = new();

        private Type _testerSelectedSignalType;
        private object _testerSignalInstance;
        private FieldInfo[] _testerSignalFields;

        private static List<MappingInfo> s_cachedMappings;
        private static List<string> s_cachedAssemblies;
        private static List<Type> s_cachedSignalTypes;

        private readonly struct MappingInfo
        {
            internal string SignalName { get; }
            internal string CommandName { get; }
            internal string Mode { get; }
            internal string Priority { get; }
            internal bool IsAsync { get; }
            internal string AssemblyName { get; }
            internal Type SignalType { get; }

            internal MappingInfo(string signalName, string commandName, string mode, string priority, bool isAsync, string assemblyName, Type signalType)
            {
                SignalName = signalName;
                CommandName = commandName;
                Mode = mode;
                Priority = priority;
                IsAsync = isAsync;
                AssemblyName = assemblyName;
                SignalType = signalType;
            }
        }

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("SIGNAL EXPLORER & PLAY-MODE TESTER");
            _view.Add(toolbar);

            var splitView = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };

            // Left Side: List of static mappings
            var leftContainer = new VisualElement { style = { width = new Length(60, LengthUnit.Percent) } };
            leftContainer.style.borderRightWidth = 1;
            leftContainer.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);

            // Filters toolbar
            var filtersToolbar = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 10, paddingRight = 10, paddingTop = 6, paddingBottom = 6, borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor), alignItems = Align.Center } };

            _searchField = new TextField { value = _searchQuery, style = { width = 130, marginRight = 10, height = 20 } };
            _searchField.RegisterValueChangedCallback(evt =>
            {
                _searchQuery = evt.newValue;
                FilterAndPopulateExplorerRows();
            });
            filtersToolbar.Add(_searchField);

            _assemblyDropdown = new DropdownField("Assembly", new List<string> { "All Assemblies" }, 0) { style = { width = 160, marginRight = 10 } };
            _assemblyDropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedAssembly = evt.newValue;
                FilterAndPopulateExplorerRows();
            });
            filtersToolbar.Add(_assemblyDropdown);

            var scanBtn = new Button(ForceScanExplorer) { text = "Refresh Cache" };
            scanBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.BtnGray);
            scanBtn.style.color = Color.white;
            scanBtn.style.fontSize = 10;
            filtersToolbar.Add(scanBtn);

            leftContainer.Add(filtersToolbar);

            // Table Headers
            var headers = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 12, paddingRight = 12, paddingTop = 6, paddingBottom = 6, backgroundColor = new StyleColor(NexusEditorStyles.TableHeaderBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderLight) } };
            headers.Add(new Label("Signal Type") { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label("Handler / Command") { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label("Mode") { style = { width = new Length(20, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            leftContainer.Add(headers);

            _explorerScrollView = new ScrollView { style = { flexGrow = 1 } };
            leftContainer.Add(_explorerScrollView);
            splitView.Add(leftContainer);

            // Right Side: tester panel
            var rightContainer = new VisualElement { style = { width = new Length(40, LengthUnit.Percent), paddingLeft = 12, paddingRight = 12, paddingTop = 10, paddingBottom = 10 } };
            var testerTitle = new Label("SIGNAL PLAY-MODE TESTER") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, color = new StyleColor(Color.gray), marginBottom = 10 } };
            rightContainer.Add(testerTitle);

            _testerPanel = new VisualElement { style = { flexGrow = 1 } };
            _testerFormContainer = new ScrollView { style = { flexGrow = 1 } };
            _testerPanel.Add(_testerFormContainer);

            _resultLogLabel = new Label { style = { fontSize = 11, marginTop = 10, whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold } };
            _testerPanel.Add(_resultLogLabel);

            rightContainer.Add(_testerPanel);
            splitView.Add(rightContainer);
            _view.Add(splitView);

            ScanExplorerAndPopulate();

            // Hook playmode state changes to update the target context dropdown
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            return _view;
        }

        public override void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange obj)
        {
            RefreshTesterView();
        }

        private void ForceScanExplorer()
        {
            s_cachedMappings = null;
            s_cachedAssemblies = null;
            ScanExplorerAndPopulate();
        }

        private void ScanExplorerAndPopulate()
        {
            _allMappings.Clear();

            if (s_cachedMappings == null)
            {
                s_cachedMappings = new List<MappingInfo>();
                s_cachedAssemblies = new List<string> { "All Assemblies" };
                s_cachedSignalTypes = new List<Type>();

                var seenSignals = new HashSet<string>();
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var uniqueAssemblies = new HashSet<string>();

                foreach (var assembly in assemblies)
                {
                    var assemblyName = assembly.GetName().Name;
                    if (assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib") || assemblyName.StartsWith("Mono") || 
                        assemblyName.StartsWith("UnityEngine") || 
                        (assemblyName.StartsWith("UnityEditor") && !assemblyName.Contains("com.nexus")))
                    {
                        continue;
                    }

                    try
                    {
                        bool hasHandlers = false;
                        foreach (var type in assembly.GetTypes())
                        {
                            if (type.IsClass && !type.IsAbstract)
                            {
                                var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                                foreach (var attr in attrs)
                                {
                                    hasHandlers = true;
                                    s_cachedMappings.Add(new MappingInfo(
                                        attr.SignalType.Name,
                                        type.Name,
                                        attr.Mode.ToString(),
                                        attr.Priority.ToString(),
                                        typeof(IAsyncCommand).IsAssignableFrom(type),
                                        assemblyName,
                                        attr.SignalType
                                    ));

                                    if (attr.SignalType != null && !seenSignals.Contains(attr.SignalType.FullName))
                                    {
                                        seenSignals.Add(attr.SignalType.FullName);
                                        s_cachedSignalTypes.Add(attr.SignalType);
                                    }
                                }

                                var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                                if (compositeAttr != null)
                                {
                                    hasHandlers = true;
                                    var sigs = new List<string>();
                                    foreach (var s in compositeAttr.SignalTypes) sigs.Add(s.Name);
                                    string compositeSigs = $"Composite({string.Join(" + ", sigs)})";

                                    s_cachedMappings.Add(new MappingInfo(
                                        compositeSigs,
                                        type.Name,
                                        compositeAttr.OneShot ? "Composite (OneShot)" : "Composite (Re-trigger)",
                                        compositeAttr.Priority.ToString(),
                                        typeof(IAsyncCommand).IsAssignableFrom(type),
                                        assemblyName,
                                        null
                                    ));
                                }
                            }
                        }

                        if (hasHandlers)
                        {
                            uniqueAssemblies.Add(assemblyName);
                        }
                    }
                    catch (ReflectionTypeLoadException) { }
                }

                s_cachedMappings.Sort((a, b) => string.Compare(a.SignalName, b.SignalName, StringComparison.OrdinalIgnoreCase));
                s_cachedSignalTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                
                foreach (var name in uniqueAssemblies)
                    s_cachedAssemblies.Add(name);
            }

            _allMappings = new List<MappingInfo>(s_cachedMappings);

            if (_assemblyDropdown != null)
            {
                _assemblyDropdown.choices = s_cachedAssemblies;
                if (!s_cachedAssemblies.Contains(_selectedAssembly))
                {
                    _selectedAssembly = "All Assemblies";
                    _assemblyDropdown.value = "All Assemblies";
                }
            }

            FilterAndPopulateExplorerRows();
            RefreshTesterView();
        }

        private void FilterAndPopulateExplorerRows()
        {
            if (_explorerScrollView == null) return;
            _explorerScrollView.Clear();
            _renderedRows.Clear();

            var filtered = new List<MappingInfo>();
            foreach (var map in _allMappings)
            {
                if (_selectedAssembly != "All Assemblies" && map.AssemblyName != _selectedAssembly)
                    continue;

                if (!string.IsNullOrEmpty(_searchQuery))
                {
                    bool matchSignal = map.SignalName.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchCommand = map.CommandName.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchSignal && !matchCommand)
                        continue;
                }

                filtered.Add(map);
            }

            bool alternate = false;
            foreach (var map in filtered)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingLeft = 8;
                row.style.paddingRight = 8;
                row.style.paddingTop = 5;
                row.style.paddingBottom = 5;
                row.style.marginTop = 2;
                row.style.marginBottom = 2;
                row.style.borderTopLeftRadius = 4;
                row.style.borderTopRightRadius = 4;
                row.style.borderBottomLeftRadius = 4;
                row.style.borderBottomRightRadius = 4;
                
                bool isSelected = _testerSelectedSignalType != null && _testerSelectedSignalType == map.SignalType;
                if (isSelected)
                    row.style.backgroundColor = new StyleColor(NexusEditorStyles.SelectedRow);
                else
                    row.style.backgroundColor = new StyleColor(alternate ? NexusEditorStyles.RowAlt : NexusEditorStyles.RowBase);
                
                alternate = !alternate;

                if (map.SignalType != null)
                {
                    row.RegisterCallback<MouseDownEvent>(evt =>
                    {
                        SelectSignalForTesting(map.SignalType);
                        evt.StopPropagation();
                    });
                }

                var l1 = new Label(map.SignalName) { style = { width = new Length(40, LengthUnit.Percent), color = new StyleColor(NexusEditorStyles.SignalBlue), unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10 } };
                
                var handlerContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, width = new Length(40, LengthUnit.Percent) } };
                var l2 = new Label(map.CommandName) { style = { color = Color.white, fontSize = 10 } };
                handlerContainer.Add(l2);

                if (map.IsAsync)
                {
                    var badge = NexusEditorStyles.CreatePill("ASYNC", NexusEditorStyles.CardBgYellow, NexusEditorStyles.AccentYellow);
                    badge.style.marginLeft = 4;
                    handlerContainer.Add(badge);
                }

                var l3 = new Label(map.Mode) { style = { width = new Length(20, LengthUnit.Percent), color = new StyleColor(NexusEditorStyles.AccentPurpleText), fontSize = 9 } };

                row.Add(l1);
                row.Add(handlerContainer);
                row.Add(l3);
                _explorerScrollView.Add(row);
                _renderedRows.Add(row);
            }
        }

        private void SelectSignalForTesting(Type signalType)
        {
            _testerSelectedSignalType = signalType;
            _testerSignalInstance = null;
            _testerSignalFields = Array.Empty<FieldInfo>();

            if (_testerSelectedSignalType != null)
            {
                try
                {
                    _testerSignalInstance = Activator.CreateInstance(_testerSelectedSignalType);
                    _testerSignalFields = _testerSelectedSignalType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                }
                catch (Exception ex)
                {
                    _resultLogLabel.text = $"Create instance error: {ex.Message}";
                    _resultLogLabel.style.color = Color.red;
                }
            }

            FilterAndPopulateExplorerRows();
            RefreshTesterView();
        }

        private void RefreshTesterView()
        {
            if (_testerFormContainer == null) return;
            _testerFormContainer.Clear();
            _resultLogLabel.text = "";

            if (!Application.isPlaying)
            {
                var label = new Label("Signal testing is only active in Play Mode. Select a signal on the left to prepare testing.") { style = { color = Color.gray, fontSize = 10, whiteSpace = WhiteSpace.Normal } };
                _testerFormContainer.Add(label);
                return;
            }

            if (_testerSelectedSignalType == null || _testerSignalInstance == null)
            {
                var label = new Label("Select a signal type from the list to test fire.") { style = { color = Color.gray, fontSize = 10 } };
                _testerFormContainer.Add(label);
                return;
            }

            // Signal Info Header
            var sigLabel = new Label($"Selected Signal: {_testerSelectedSignalType.Name}") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, color = Color.white, marginBottom = 8 } };
            _testerFormContainer.Add(sigLabel);

            // Dynamic fields list
            foreach (var field in _testerSignalFields)
            {
                var element = CreateSignalFieldUI(field, field.FieldType, () => field.GetValue(_testerSignalInstance), val => field.SetValue(_testerSignalInstance, val));
                if (element != null) _testerFormContainer.Add(element);
            }

            // Context target selection (addresses multi-context concerns!)
            var activeContexts = NexusRuntime.ActiveContexts;
            var contextChoices = activeContexts.Select(c => c is Context ctx ? ctx.ScopeTag : "Unknown").ToList();
            
            if (contextChoices.Count == 0)
            {
                var noContextLabel = new Label("No active Contexts available. Play Mode target signal bus is missing.") { style = { color = Color.red, fontSize = 9 } };
                _testerFormContainer.Add(noContextLabel);
                return;
            }

            var defaultContext = contextChoices[0];
            _contextTargetDropdown = new DropdownField("Target Context", contextChoices, 0);
            _testerFormContainer.Add(_contextTargetDropdown);

            // Fire Button
            _fireButton = NexusEditorStyles.CreateButton("Fire Test Signal", FireSelectedSignal, NexusEditorStyles.BtnGreen);
            _fireButton.style.marginTop = 10;
            _fireButton.style.height = 30;
            _testerFormContainer.Add(_fireButton);
        }

        private VisualElement CreateSignalFieldUI(FieldInfo field, Type type, Func<object> getter, Action<object> setter)
        {
            object initialValue = null;
            try { initialValue = getter(); } catch { }

            if (type == typeof(int))
            {
                var ui = new IntegerField(field.Name) { value = (int)(initialValue ?? 0) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(float))
            {
                var ui = new FloatField(field.Name) { value = (float)(initialValue ?? 0f) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(double))
            {
                var ui = new DoubleField(field.Name) { value = (double)(initialValue ?? 0.0) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(bool))
            {
                var ui = new Toggle(field.Name) { value = (bool)(initialValue ?? false) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(string))
            {
                var ui = new TextField(field.Name) { value = (string)initialValue ?? "" };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(Vector2))
            {
                var ui = new Vector2Field(field.Name) { value = (Vector2)(initialValue ?? Vector2.zero) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(Vector3))
            {
                var ui = new Vector3Field(field.Name) { value = (Vector3)(initialValue ?? Vector3.zero) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type == typeof(Color))
            {
                var ui = new ColorField(field.Name) { value = (Color)(initialValue ?? Color.white) };
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }
            if (type.IsEnum)
            {
                var ui = new EnumField(field.Name, (Enum)(initialValue ?? Enum.GetValues(type).GetValue(0)));
                ui.RegisterValueChangedCallback(evt => setter(evt.newValue));
                return ui;
            }

            return new Label($"{field.Name}: {initialValue ?? "null"} (Unsupported Type)");
        }

        private void FireSelectedSignal()
        {
            if (_contextTargetDropdown == null || string.IsNullOrEmpty(_contextTargetDropdown.value))
            {
                _resultLogLabel.text = "Error: Target context not selected.";
                _resultLogLabel.style.color = Color.red;
                return;
            }

            try
            {
                var activeContexts = NexusRuntime.ActiveContexts;
                Context targetContext = null;
                
                foreach (var ctx in activeContexts)
                {
                    if (ctx is Context context && context.ScopeTag == _contextTargetDropdown.value)
                    {
                        targetContext = context;
                        break;
                    }
                }

                if (targetContext == null || targetContext.SignalBus == null)
                {
                    _resultLogLabel.text = $"Error: Target context '{_contextTargetDropdown.value}' or SignalBus is offline.";
                    _resultLogLabel.style.color = Color.yellow;
                    return;
                }

                var targetBus = targetContext.SignalBus as SignalBus;
                if (targetBus == null)
                {
                    _resultLogLabel.text = "Error: Invalid SignalBus implementation.";
                    _resultLogLabel.style.color = Color.red;
                    return;
                }

                var fireGeneric = typeof(SignalBus).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Fire" && m.IsGenericMethodDefinition);
                var fireMethod = fireGeneric.MakeGenericMethod(_testerSelectedSignalType);

                fireMethod.Invoke(targetBus, new[] { _testerSignalInstance });

                _resultLogLabel.text = $"\u2713 Fired: {_testerSelectedSignalType.Name} on context '{targetContext.ScopeTag}'";
                _resultLogLabel.style.color = NexusEditorStyles.AccentGreen;
            }
            catch (Exception ex)
            {
                _resultLogLabel.text = $"Fire failed: {ex.Message}";
                _resultLogLabel.style.color = Color.red;
                Debug.LogException(ex);
            }
        }
    }
}
