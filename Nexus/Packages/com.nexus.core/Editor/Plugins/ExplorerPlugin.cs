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
        public override string DisplayName => NexusLang.Get("action_explorer_title");
        public override int Order => 3;

        private VisualElement _view;
        private ScrollView _explorerScrollView;
        private DropdownField _assemblyDropdown;
        private TextField _searchField;

        private VisualElement _testerPanel;
        private ScrollView _testerFormContainer;
        private DropdownField _contextTargetDropdown;
        private Button _refreshTargetsButton;
        private Button _fireButton;
        private Label _resultLogLabel;

        private string _searchQuery = "";
        private string _selectedAssembly = "All Assemblies";
        private List<MappingInfo> _allMappings = new();
        private readonly List<VisualElement> _renderedRows = new();

        private enum ExplorerTab { Signals, LiveModels }
        private ExplorerTab _selectedTab = ExplorerTab.Signals;
        private VisualElement _tabContent;

        private Type _testerSelectedSignalType;
        private object _testerSignalInstance;
        private FieldInfo[] _testerSignalFields;

        private static List<MappingInfo> s_cachedMappings;
        private static List<string> s_cachedAssemblies;
        private static List<Type> s_cachedSignalTypes;

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_cachedMappings = null;
            s_cachedAssemblies = null;
            s_cachedSignalTypes = null;
        }

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

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("explorer_title"));
            _view.Add(toolbar);

            var tabHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) } };
            
            var btnSignals = CreateTabButton(NexusLang.Get("explorer_tab_signals"), ExplorerTab.Signals);
            var btnModels = CreateTabButton(NexusLang.Get("explorer_tab_models"), ExplorerTab.LiveModels);

            tabHeader.Add(btnSignals);
            tabHeader.Add(btnModels);
            _view.Add(tabHeader);

            _tabContent = new VisualElement { style = { flexGrow = 1 } };
            _view.Add(_tabContent);

            ScanExplorerAndPopulate();

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            
            RenderTab();

            return _view;
        }

        public override System.Collections.Generic.IReadOnlyList<(string Label, System.Action Action, UnityEngine.Color Color)> GetContextActions()
            => new System.Collections.Generic.List<(string, System.Action, UnityEngine.Color)>
            {
                ("⚡ CodeGen",        () => NexusCodeGenerator.GenerateBinder(),         NexusEditorStyles.BtnBlue),
                ("🔍 Inspector",      () => Window?.SwitchToPlugin("ContextInspector"),  NexusEditorStyles.BtnPurple),
                ("🔄 Rescan",         () => { ScanExplorerAndPopulate(); RenderTab(); }, NexusEditorStyles.BtnGray),
            };

        private Button CreateTabButton(string label, ExplorerTab tab)
        {
            var btn = new Button(() =>
            {
                _selectedTab = tab;
                RenderTab();
            }) { text = label };

            btn.name = $"Tab_{(int)tab}";
            btn.style.backgroundColor = new StyleColor(Color.clear);
            btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            btn.style.paddingLeft = 12;
            btn.style.paddingRight = 12;
            btn.style.paddingTop = 8;
            btn.style.paddingBottom = 8;
            btn.style.fontSize = 11;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;

            return btn;
        }

        private void RenderTab()
        {
            if (_tabContent == null) return;
            _tabContent.Clear();

            foreach (ExplorerTab tab in Enum.GetValues(typeof(ExplorerTab)))
            {
                var btn = _view.Q<Button>($"Tab_{(int)tab}");
                if (btn != null)
                {
                    if (tab == _selectedTab)
                    {
                        btn.style.backgroundColor = new StyleColor(NexusEditorStyles.HighlightBg);
                        btn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                    }
                    else
                    {
                        btn.style.backgroundColor = new StyleColor(Color.clear);
                        btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
                    }
                }
            }

            if (_selectedTab == ExplorerTab.Signals)
                BuildSignalsTab();
            else
                BuildLiveModelsTab();
        }

        private void BuildSignalsTab()
        {
            var splitView = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };

            var leftContainer = new VisualElement { style = { width = new Length(60, LengthUnit.Percent) } };
            leftContainer.style.borderRightWidth = 1;
            leftContainer.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);

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

            var headers = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 12, paddingRight = 12, paddingTop = 6, paddingBottom = 6, backgroundColor = new StyleColor(NexusEditorStyles.TableHeaderBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderLight) } };
            headers.Add(new Label(NexusLang.Get("explorer_signal_type")) { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label(NexusLang.Get("explorer_handler_command")) { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label(NexusLang.Get("explorer_mode")) { style = { width = new Length(20, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            leftContainer.Add(headers);

            _explorerScrollView = new ScrollView { style = { flexGrow = 1 } };
            leftContainer.Add(_explorerScrollView);
            splitView.Add(leftContainer);

            var rightContainer = new VisualElement { style = { width = new Length(40, LengthUnit.Percent), paddingLeft = 12, paddingRight = 12, paddingTop = 10, paddingBottom = 10 } };
            var testerTitle = new Label(NexusLang.Get("explorer_tester_title")) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, color = new StyleColor(Color.gray), marginBottom = 10 } };
            rightContainer.Add(testerTitle);

            _testerPanel = new VisualElement { style = { flexGrow = 1 } };
            _testerFormContainer = new ScrollView { style = { flexGrow = 1 } };
            _testerPanel.Add(_testerFormContainer);

            _resultLogLabel = new Label { style = { fontSize = 11, marginTop = 10, whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Bold } };
            _testerPanel.Add(_resultLogLabel);

            rightContainer.Add(_testerPanel);
            splitView.Add(rightContainer);
            _tabContent.Add(splitView);

            FilterAndPopulateExplorerRows();
            RefreshTesterView();
        }

        private void BuildLiveModelsTab()
        {
            var container = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 10 } };
            
            if (!Application.isPlaying)
            {
                container.Add(CreateStateLine(NexusLang.Get("explorer_live_models_hint"), NexusEditorStyles.TextSecondary));
                _tabContent.Add(container);
                return;
            }

            var activeContexts = NexusRuntime.ActiveContexts;
            if (activeContexts.Count == 0)
            {
                container.Add(CreateStateLine(NexusLang.Get("explorer_no_contexts"), NexusEditorStyles.TextSecondary));
                _tabContent.Add(container);
                return;
            }

            var refreshBtn = NexusEditorStyles.CreateButton(NexusLang.Get("explorer_refresh"), RenderTab, NexusEditorStyles.BtnBlue);
            refreshBtn.style.alignSelf = Align.FlexStart;
            refreshBtn.style.marginBottom = 10;
            container.Add(refreshBtn);

            foreach (var ctx in activeContexts)
            {
                if (ctx is not Context castedCtx) continue;
                var contextData = castedCtx.ContextData;
                string ctxName = contextData != null ? contextData.name.Replace("ContextData", "") : "Unnamed Context";

                var foldout = new Foldout { text = $"Context: {ctxName} ({castedCtx.ScopeTag})", value = true };
                foldout.style.backgroundColor = new StyleColor(NexusEditorStyles.CardBg);
                foldout.style.borderBottomWidth = 1;
                foldout.style.borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor);
                foldout.style.marginBottom = 5;
                foldout.style.paddingLeft = 5;
                foldout.style.paddingTop = 5;

                var containerField = typeof(NexusDI).GetField("_bindings", BindingFlags.NonPublic | BindingFlags.Instance);
                if (containerField != null)
                {
                    var bindings = containerField.GetValue(castedCtx.Container) as System.Collections.IDictionary;
                    if (bindings != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in bindings)
                        {
                            Type interfaceType = entry.Key as Type;
                            object bindingObj = entry.Value;
                            if (bindingObj == null) continue;
                            
                            var instanceProp = bindingObj.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.Instance);
                            if (instanceProp != null)
                            {
                                object instance = instanceProp.GetValue(bindingObj);
                                if (instance != null)
                                {
                                    if (instance is IContext || instance is NexusDI || instance is CommandPoolManager || instance is ISignalBus)
                                        continue;

                                    var instanceType = instance.GetType();
                                    
                                    var modelFoldout = new Foldout { text = $"{interfaceType.Name} -> {instanceType.Name}", value = false };
                                    modelFoldout.style.marginLeft = 15;
                                    modelFoldout.style.color = new StyleColor(NexusEditorStyles.AccentGreen);

                                    var props = instanceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                    foreach (var prop in props)
                                    {
                                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                                        try
                                        {
                                            object val = prop.GetValue(instance);
                                            string valStr = val != null ? val.ToString() : "null";
                                            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginLeft = 15 } };
                                            row.Add(new Label(prop.Name) { style = { width = 150, color = NexusEditorStyles.TextSecondary } });
                                            row.Add(new Label(valStr) { style = { color = Color.white } });
                                            modelFoldout.Add(row);
                                        }
                                        catch { }
                                    }
                                    
                                    foldout.Add(modelFoldout);
                                }
                            }
                        }
                    }
                }
                
                container.Add(foldout);
            }

            _tabContent.Add(container);
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
                var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
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
                    string q = _searchQuery.Trim().ToLowerInvariant();
                    if (q.StartsWith("t:"))
                    {
                        if (map.SignalName.IndexOf(q.Substring(2).Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    else if (q.StartsWith("c:"))
                    {
                        if (map.CommandName.IndexOf(q.Substring(2).Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    else if (q.StartsWith("m:"))
                    {
                        if (map.Mode.IndexOf(q.Substring(2).Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    else
                    {
                        bool matchSignal = map.SignalName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool matchCommand = map.CommandName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!matchSignal && !matchCommand)
                            continue;
                    }
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

                var modeContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, width = new Length(20, LengthUnit.Percent) } };
                var l3 = new Label(map.Mode) { style = { color = new StyleColor(NexusEditorStyles.AccentPurpleText), fontSize = 9, flexGrow = 1 } };
                modeContainer.Add(l3);

                string sigName = map.SignalName;
                string cmdName = map.CommandName;

                var copyBtn = new Button(() => { EditorGUIUtility.systemCopyBuffer = sigName; })
                {
                    text = "📋",
                    tooltip = "Copy Signal Name",
                    style = { fontSize = 8, width = 18, height = 16, marginRight = 2, paddingLeft = 0, paddingRight = 0, backgroundColor = new StyleColor(NexusEditorStyles.BtnGray), color = Color.white }
                };
                modeContainer.Add(copyBtn);

                var openBtn = new Button(() =>
                {
                    string targetName = cmdName != "No Handler" ? cmdName : sigName;
                    var guids = AssetDatabase.FindAssets($"{targetName} t:Script");
                    if (guids.Length > 0)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                        if (obj != null) AssetDatabase.OpenAsset(obj);
                    }
                })
                {
                    text = "🔍",
                    tooltip = "Open Script in IDE",
                    style = { fontSize = 8, width = 18, height = 16, paddingLeft = 0, paddingRight = 0, backgroundColor = new StyleColor(NexusEditorStyles.BtnBlue), color = Color.white }
                };
                modeContainer.Add(openBtn);

                row.Add(l1);
                row.Add(handlerContainer);
                row.Add(modeContainer);
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
                    _resultLogLabel.text = string.Format(NexusLang.Get("explorer_create_error"), ex.Message);
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
                _testerFormContainer.Add(CreateStateLine(NexusLang.Get("explorer_testing_hint"), Color.gray));
                return;
            }

            if (_testerSelectedSignalType == null || _testerSignalInstance == null)
            {
                _testerFormContainer.Add(CreateStateLine(NexusLang.Get("explorer_select_signal"), Color.gray));
                return;
            }

            _testerFormContainer.Add(CreateSectionHeader(string.Format(NexusLang.Get("explorer_selected_signal"), _testerSelectedSignalType.Name)));

            foreach (var field in _testerSignalFields)
            {
                var element = CreateSignalFieldUI(field, field.FieldType, () => field.GetValue(_testerSignalInstance), val => field.SetValue(_testerSignalInstance, val));
                if (element != null) _testerFormContainer.Add(element);
            }

            var activeContexts = NexusRuntime.ActiveContexts;
            var contextChoices = activeContexts.Select(c => c is Context ctx ? ctx.ScopeTag : "Unknown").ToList();

            var topActions = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 6 } };
            topActions.Add(NexusEditorStyles.CreateButton("Tracer", () => Window?.OpenPlugin("Tracer"), NexusEditorStyles.BtnTeal));
            topActions.Add(NexusEditorStyles.CreateButton("Game Manager", () => Window?.OpenPlugin("GameManager"), NexusEditorStyles.BtnBlue));
            topActions.Add(NexusEditorStyles.CreateButton("Refresh", RefreshTesterView, NexusEditorStyles.BtnGray));
            _testerFormContainer.Add(topActions);

            if (contextChoices.Count == 0)
            {
                _testerFormContainer.Add(CreateStateLine(NexusLang.Get("explorer_no_context_target"), Color.red));
                return;
            }

            _contextTargetDropdown = new DropdownField("Target Context", contextChoices, 0);
            _contextTargetDropdown.tooltip = "Fire the test signal into the selected context";
            _testerFormContainer.Add(_contextTargetDropdown);

            _refreshTargetsButton = NexusEditorStyles.CreateButton("Refresh Targets", RefreshTesterView, NexusEditorStyles.BtnGray);
            _refreshTargetsButton.style.marginTop = 4;
            _testerFormContainer.Add(_refreshTargetsButton);

            _fireButton = NexusEditorStyles.CreateButton(NexusLang.Get("explorer_fire_test"), FireSelectedSignal, NexusEditorStyles.BtnGreen);
            _fireButton.tooltip = "Fire the selected signal into the target context";
            _fireButton.style.marginTop = 10;
            _fireButton.style.height = 30;
            _testerFormContainer.Add(_fireButton);

            _testerFormContainer.Add(CreateSectionHeader(NexusLang.Get("explorer_presets")));

            var presetRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            var presetNameField = new TextField { style = { flexGrow = 1, marginRight = 5 } };
            presetNameField.SetValueWithoutNotify("Default");
            presetRow.Add(presetNameField);

            var savePresetBtn = new Button(() => SavePreset(presetNameField.value)) { text = "Save" };
            presetRow.Add(savePresetBtn);

            _testerFormContainer.Add(presetRow);

            var loadPresetRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            var presetNames = GetSavedPresetNames();
            if (presetNames.Count == 0) presetNames.Add("No Presets");
            var loadDropdown = new DropdownField(presetNames, 0) { style = { flexGrow = 1, marginRight = 5 } };
            loadPresetRow.Add(loadDropdown);

            var loadBtn = new Button(() => LoadPreset(loadDropdown.value)) { text = "Load" };
            if (presetNames[0] == "No Presets") loadBtn.SetEnabled(false);
            loadPresetRow.Add(loadBtn);

            _testerFormContainer.Add(loadPresetRow);
        }

        private void SavePreset(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName)) return;
            if (_testerSelectedSignalType == null || _testerSignalInstance == null) return;

            string key = $"NexusPreset_{_testerSelectedSignalType.FullName}_{presetName}";
            string json = EditorJsonUtility.ToJson(_testerSignalInstance);
            EditorPrefs.SetString(key, json);

            string listKey = $"NexusPresets_{_testerSelectedSignalType.FullName}";
            var list = GetSavedPresetNames();
            if (!list.Contains(presetName))
            {
                list.Add(presetName);
                EditorPrefs.SetString(listKey, string.Join(";", list));
            }
            RefreshTesterView();
        }

        private List<string> GetSavedPresetNames()
        {
            if (_testerSelectedSignalType == null) return new List<string>();
            string listKey = $"NexusPresets_{_testerSelectedSignalType.FullName}";
            string saved = EditorPrefs.GetString(listKey, "");
            if (string.IsNullOrEmpty(saved)) return new List<string>();
            return new List<string>(saved.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        private void LoadPreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName) || presetName == "No Presets") return;
            if (_testerSelectedSignalType == null || _testerSignalInstance == null) return;

            string key = $"NexusPreset_{_testerSelectedSignalType.FullName}_{presetName}";
            string json = EditorPrefs.GetString(key, "");
            if (!string.IsNullOrEmpty(json))
            {
                EditorJsonUtility.FromJsonOverwrite(json, _testerSignalInstance);
                RefreshTesterView();
            }
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

            return new Label(string.Format(NexusLang.Get("explorer_unsupported_type"), field.Name, initialValue ?? "null"));
        }

        private VisualElement CreateSectionHeader(string text)
        {
            return new Label(text)
            {
                style = { marginTop = 15, fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray }
            };
        }

        private VisualElement CreateStateLine(string text, Color color)
        {
            return new Label(text)
            {
                style = { color = color, fontSize = 10, whiteSpace = WhiteSpace.Normal }
            };
        }

        private void FireSelectedSignal()
        {
            if (_contextTargetDropdown == null || string.IsNullOrEmpty(_contextTargetDropdown.value))
            {
                _resultLogLabel.text = NexusLang.Get("explorer_error_context");
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

                if (targetContext == null)
                {
                    targetContext = activeContexts.OfType<Context>().FirstOrDefault();
                }

                if (targetContext == null || targetContext.SignalBus == null)
                {
                    _resultLogLabel.text = string.Format(NexusLang.Get("explorer_error_offline"), _contextTargetDropdown.value);
                    _resultLogLabel.style.color = Color.yellow;
                    return;
                }

                var targetBus = targetContext.SignalBus as SignalBus;
                if (targetBus == null)
                {
                    _resultLogLabel.text = NexusLang.Get("explorer_error_invalid_bus");
                    _resultLogLabel.style.color = Color.red;
                    return;
                }

                var fireGeneric = typeof(SignalBus).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Fire" && m.IsGenericMethodDefinition);
                var fireMethod = fireGeneric.MakeGenericMethod(_testerSelectedSignalType);

                fireMethod.Invoke(targetBus, new[] { _testerSignalInstance });

                _resultLogLabel.text = string.Format(NexusLang.Get("explorer_fired"), _testerSelectedSignalType.Name, targetContext.ScopeTag);
                _resultLogLabel.style.color = NexusEditorStyles.AccentGreen;
            }
            catch (Exception ex)
            {
                _resultLogLabel.text = string.Format(NexusLang.Get("explorer_fire_failed"), ex.Message);
                _resultLogLabel.style.color = Color.red;
                Debug.LogException(ex);
            }
        }
    }
}
