using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System;
using System.Reflection;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Editor
{
    public class SignalExplorerWindow : EditorWindow
    {
        private ScrollView _scrollView;
        private readonly List<VisualElement> _renderedRows = new();
        private List<MappingInfo> _allMappings = new();
        
        private string _searchQuery = "";
        private string _selectedAssembly = "All Assemblies";
        private DropdownField _assemblyDropdown;
        private TextField _searchField;

        [MenuItem("Window/Nexus/Signal Explorer")]
        public static void ShowWindow()
        {
            var window = GetWindow<SignalExplorerWindow>("Nexus Signal Explorer");
            window.minSize = new Vector2(500, 400);
            window.Show();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.14f));

            // Toolbar
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.paddingLeft = 10;
            toolbar.style.paddingRight = 10;
            toolbar.style.paddingTop = 8;
            toolbar.style.paddingBottom = 8;
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new StyleColor(new Color(0.2f, 0.2f, 0.22f));
            toolbar.style.alignItems = Align.Center;
            toolbar.style.flexWrap = Wrap.Wrap;

            var titleLabel = new Label("SIGNAL-COMMAND STATIC MAP");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleLabel.style.marginRight = 15;
            toolbar.Add(titleLabel);

            // Search query textfield
            _searchField = new TextField();
            _searchField.style.width = 150;
            _searchField.style.marginRight = 10;
            _searchField.style.height = 20;
            _searchField.RegisterValueChangedCallback(evt => {
                _searchQuery = evt.newValue;
                FilterAndPopulateUI();
            });

            var searchPlaceholder = new Label("Search...");
            searchPlaceholder.style.position = Position.Absolute;
            searchPlaceholder.style.left = 8;
            searchPlaceholder.style.top = 2;
            searchPlaceholder.style.fontSize = 11;
            searchPlaceholder.style.color = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            searchPlaceholder.style.unityFontStyleAndWeight = FontStyle.Italic;
            searchPlaceholder.pickingMode = PickingMode.Ignore;
            _searchField.Add(searchPlaceholder);
            _searchField.RegisterCallback<FocusInEvent>(evt => searchPlaceholder.style.display = DisplayStyle.None);
            _searchField.RegisterCallback<FocusOutEvent>(evt =>
            {
                if (string.IsNullOrEmpty(_searchField.value))
                    searchPlaceholder.style.display = DisplayStyle.Flex;
            });
            toolbar.Add(_searchField);

            // Dropdown field for assembly
            _assemblyDropdown = new DropdownField("Assembly", new List<string> { "All Assemblies" }, 0);
            _assemblyDropdown.style.width = 200;
            _assemblyDropdown.style.marginRight = 10;
            _assemblyDropdown.RegisterValueChangedCallback(evt => {
                _selectedAssembly = evt.newValue;
                FilterAndPopulateUI();
            });
            toolbar.Add(_assemblyDropdown);

            var scanButton = new Button(ScanAndPopulate) { text = "Scan Assemblies" };
            scanButton.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            scanButton.style.borderTopLeftRadius = 4;
            scanButton.style.borderTopRightRadius = 4;
            scanButton.style.borderBottomLeftRadius = 4;
            scanButton.style.borderBottomRightRadius = 4;
            scanButton.style.color = Color.white;
            scanButton.style.paddingLeft = 10;
            scanButton.style.paddingRight = 10;
            toolbar.Add(scanButton);

            root.Add(toolbar);

            // Table Headers
            var headers = new VisualElement();
            headers.style.flexDirection = FlexDirection.Row;
            headers.style.paddingLeft = 15;
            headers.style.paddingRight = 15;
            headers.style.paddingTop = 6;
            headers.style.paddingBottom = 6;
            headers.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.18f));
            headers.style.borderBottomWidth = 1;
            headers.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.27f));

            var col1 = new Label("Signal Type") { style = { width = new Length(35, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 10 } };
            var col2 = new Label("Handler / Command") { style = { width = new Length(35, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 10 } };
            var col3 = new Label("Execution Mode") { style = { width = new Length(18, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 10 } };
            var col4 = new Label("Priority") { style = { width = new Length(12, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 10 } };

            headers.Add(col1);
            headers.Add(col2);
            headers.Add(col3);
            headers.Add(col4);
            root.Add(headers);

            // Scrollview
            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            _scrollView.style.paddingLeft = 10;
            _scrollView.style.paddingRight = 10;
            _scrollView.style.paddingTop = 5;
            _scrollView.style.paddingBottom = 10;
            root.Add(_scrollView);

            ScanAndPopulate();
        }

        private void ScanAndPopulate()
        {
            _allMappings.Clear();
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
                                _allMappings.Add(new MappingInfo(
                                    attr.SignalType.Name,
                                    type.Name,
                                    attr.Mode.ToString(),
                                    attr.Priority.ToString(),
                                    typeof(IAsyncCommand).IsAssignableFrom(type),
                                    assemblyName
                                ));
                            }

                            var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                            if (compositeAttr != null)
                            {
                                hasHandlers = true;
                                var sigs = new List<string>();
                                foreach (var s in compositeAttr.SignalTypes) sigs.Add(s.Name);
                                string compositeSigs = $"Composite({string.Join(" + ", sigs)})";

                                _allMappings.Add(new MappingInfo(
                                    compositeSigs,
                                    type.Name,
                                    compositeAttr.OneShot ? "Composite (OneShot)" : "Composite (Re-trigger)",
                                    compositeAttr.Priority.ToString(),
                                    typeof(IAsyncCommand).IsAssignableFrom(type),
                                    assemblyName
                                ));
                            }
                        }
                    }

                    if (hasHandlers)
                    {
                        uniqueAssemblies.Add(assemblyName);
                    }
                }
                catch
                {
                    // Ignore unloadable assemblies
                }
            }

            _allMappings.Sort((a, b) => string.Compare(a.SignalName, b.SignalName, StringComparison.OrdinalIgnoreCase));

            var choices = new List<string> { "All Assemblies" };
            foreach (var name in uniqueAssemblies)
            {
                choices.Add(name);
            }
            if (_assemblyDropdown != null)
            {
                _assemblyDropdown.choices = choices;
                if (!choices.Contains(_selectedAssembly))
                {
                    _selectedAssembly = "All Assemblies";
                    _assemblyDropdown.value = "All Assemblies";
                }
            }

            FilterAndPopulateUI();
        }

        private void FilterAndPopulateUI()
        {
            _scrollView.Clear();
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

            if (filtered.Count == 0)
            {
                var emptyContainer = new VisualElement();
                emptyContainer.style.alignItems = Align.Center;
                emptyContainer.style.marginTop = 30;
                emptyContainer.style.paddingLeft = 20;
                emptyContainer.style.paddingRight = 20;

                var noItems = new Label("No matching SignalHandlers found.") { 
                    style = { 
                        color = Color.gray, 
                        fontSize = 12, 
                        unityFontStyleAndWeight = FontStyle.Bold,
                        marginBottom = 10 
                    } 
                };
                emptyContainer.Add(noItems);

                var helpLink = new Button(() => {
                    Application.OpenURL("https://github.com/CevatAYDN/GameContainer");
                }) { text = "Learn how to register Signal Handlers (Documentation)" };
                helpLink.style.backgroundColor = new StyleColor(new Color(0.2f, 0.35f, 0.5f));
                helpLink.style.color = Color.white;
                helpLink.style.paddingLeft = 12;
                helpLink.style.paddingRight = 12;
                helpLink.style.paddingTop = 6;
                helpLink.style.paddingBottom = 6;
                helpLink.style.borderTopLeftRadius = 4;
                helpLink.style.borderTopRightRadius = 4;
                helpLink.style.borderBottomLeftRadius = 4;
                helpLink.style.borderBottomRightRadius = 4;
                emptyContainer.Add(helpLink);

                _scrollView.Add(emptyContainer);
                return;
            }

            bool alternate = false;
            foreach (var map in filtered)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingLeft = 8;
                row.style.paddingRight = 8;
                row.style.paddingTop = 6;
                row.style.paddingBottom = 6;
                row.style.marginTop = 2;
                row.style.marginBottom = 2;
                row.style.borderTopLeftRadius = 4;
                row.style.borderTopRightRadius = 4;
                row.style.borderBottomLeftRadius = 4;
                row.style.borderBottomRightRadius = 4;
                row.style.backgroundColor = new StyleColor(alternate ? new Color(0.15f, 0.15f, 0.17f) : new Color(0.18f, 0.18f, 0.2f));
                alternate = !alternate;

                var l1 = new Label(map.SignalName) { style = { width = new Length(35, LengthUnit.Percent), color = new Color(0.7f, 0.85f, 1f), unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } };
                
                var handlerContainer = new VisualElement();
                handlerContainer.style.flexDirection = FlexDirection.Row;
                handlerContainer.style.alignItems = Align.Center;
                handlerContainer.style.width = new Length(35, LengthUnit.Percent);

                var l2 = new Label(map.CommandName) { style = { color = Color.white, fontSize = 11 } };
                handlerContainer.Add(l2);

                if (map.IsAsync)
                {
                    var badge = new Label("⚡ ASYNC") {
                        style = {
                            fontSize = 8,
                            backgroundColor = new StyleColor(new Color(0.45f, 0.35f, 0.15f)),
                            color = new StyleColor(new Color(1f, 0.8f, 0.2f)),
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
                    handlerContainer.Add(badge);
                }

                var l3 = new Label(map.Mode) { style = { width = new Length(18, LengthUnit.Percent), color = new Color(0.8f, 0.6f, 0.9f), fontSize = 10 } };
                var l4 = new Label(map.Priority) { style = { width = new Length(12, LengthUnit.Percent), color = new Color(1f, 0.8f, 0.4f), unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } };

                row.Add(l1);
                row.Add(handlerContainer);
                row.Add(l3);
                row.Add(l4);
                _scrollView.Add(row);
                _renderedRows.Add(row);
            }
        }

        private struct MappingInfo
        {
            public string SignalName;
            public string CommandName;
            public string Mode;
            public string Priority;
            public bool IsAsync;
            public string AssemblyName;

            public MappingInfo(string signalName, string commandName, string mode, string priority, bool isAsync, string assemblyName)
            {
                SignalName = signalName;
                CommandName = commandName;
                Mode = mode;
                Priority = priority;
                IsAsync = isAsync;
                AssemblyName = assemblyName;
            }
        }
    }
}
