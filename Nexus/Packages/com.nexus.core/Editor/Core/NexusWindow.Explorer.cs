using System;
using System.Collections.Generic;
using System.Linq;
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
        // ── TAB 4: SIGNAL EXPLORER & TESTER
        // ==========================================
        private void BuildExplorerTab()
        {
            var toolbar = NexusEditorStyles.CreateToolbar("SIGNAL EXPLORER & PLAY-MODE TESTER");
            _contentArea.Add(toolbar);

            // Split View layout
            var splitView = new VisualElement();
            splitView.style.flexDirection = FlexDirection.Row;
            splitView.style.flexGrow = 1;

            // Left Side: List of static mappings
            var leftContainer = new VisualElement();
            leftContainer.style.width = new Length(60, LengthUnit.Percent);
            leftContainer.style.borderRightWidth = 1;
            leftContainer.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);

            // Filters toolbar
            var filtersToolbar = new VisualElement();
            filtersToolbar.style.flexDirection = FlexDirection.Row;
            filtersToolbar.style.paddingLeft = 10;
            filtersToolbar.style.paddingRight = 10;
            filtersToolbar.style.paddingTop = 6;
            filtersToolbar.style.paddingBottom = 6;
            filtersToolbar.style.borderBottomWidth = 1;
            filtersToolbar.style.borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor);
            filtersToolbar.style.alignItems = Align.Center;

            _explorerSearchField = new TextField { value = _explorerSearchQuery };
            _explorerSearchField.style.width = 130;
            _explorerSearchField.style.marginRight = 10;
            _explorerSearchField.style.height = 20;
            _explorerSearchField.RegisterValueChangedCallback(evt =>
            {
                _explorerSearchQuery = evt.newValue;
                FilterAndPopulateExplorerRows();
            });
            filtersToolbar.Add(_explorerSearchField);

            _explorerAssemblyDropdown = new DropdownField("Assembly", new List<string> { "All Assemblies" }, 0);
            _explorerAssemblyDropdown.style.width = 160;
            _explorerAssemblyDropdown.style.marginRight = 10;
            _explorerAssemblyDropdown.RegisterValueChangedCallback(evt =>
            {
                _explorerSelectedAssembly = evt.newValue;
                FilterAndPopulateExplorerRows();
            });
            filtersToolbar.Add(_explorerAssemblyDropdown);

            var scanBtn = new Button(ForceScanExplorer) { text = "Refresh Cache" };
            scanBtn.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            scanBtn.style.color = Color.white;
            scanBtn.style.fontSize = 10;
            filtersToolbar.Add(scanBtn);

            leftContainer.Add(filtersToolbar);

            // Table Headers
            var headers = new VisualElement();
            headers.style.flexDirection = FlexDirection.Row;
            headers.style.paddingLeft = 12;
            headers.style.paddingRight = 12;
            headers.style.paddingTop = 6;
            headers.style.paddingBottom = 6;
            headers.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.18f));
            headers.style.borderBottomWidth = 1;
            headers.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.27f));

            headers.Add(new Label("Signal Type") { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label("Handler / Command") { style = { width = new Length(40, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            headers.Add(new Label("Mode") { style = { width = new Length(20, LengthUnit.Percent), unityFontStyleAndWeight = FontStyle.Bold, color = Color.gray, fontSize = 9 } });
            leftContainer.Add(headers);

            _explorerScrollView = new ScrollView();
            _explorerScrollView.style.flexGrow = 1;
            leftContainer.Add(_explorerScrollView);
            splitView.Add(leftContainer);

            // Right Side: tester panel
            var rightContainer = new VisualElement();
            rightContainer.style.width = new Length(40, LengthUnit.Percent);
            rightContainer.style.paddingLeft = 12;
            rightContainer.style.paddingRight = 12;
            rightContainer.style.paddingTop = 10;
            rightContainer.style.paddingBottom = 10;

            var testerTitle = new Label("SIGNAL PLAY-MODE TESTER");
            testerTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            testerTitle.style.fontSize = 12;
            testerTitle.style.color = new StyleColor(Color.gray);
            testerTitle.style.marginBottom = 10;
            rightContainer.Add(testerTitle);

            var testerView = new IMGUIContainer(DrawSignalTesterIMGUI);
            testerView.style.flexGrow = 1;
            rightContainer.Add(testerView);

            splitView.Add(rightContainer);

            _contentArea.Add(splitView);

            // Scan and populate
            ScanExplorerAndPopulate();
        }

        private void ForceScanExplorer()
        {
            s_cachedMappings = null;
            s_cachedAssemblies = null;
            ScanExplorerAndPopulate();
        }

        private void ScanExplorerAndPopulate()
        {
            _explorerAllMappings.Clear();

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

            _explorerAllMappings = new List<MappingInfo>(s_cachedMappings);

            if (_explorerAssemblyDropdown != null)
            {
                _explorerAssemblyDropdown.choices = s_cachedAssemblies;
                if (!s_cachedAssemblies.Contains(_explorerSelectedAssembly))
                {
                    _explorerSelectedAssembly = "All Assemblies";
                    _explorerAssemblyDropdown.value = "All Assemblies";
                }
            }

            FilterAndPopulateExplorerRows();
        }

        private void FilterAndPopulateExplorerRows()
        {
            if (_explorerScrollView == null) return;
            _explorerScrollView.Clear();
            _explorerRenderedRows.Clear();

            var filtered = new List<MappingInfo>();
            foreach (var map in _explorerAllMappings)
            {
                if (_explorerSelectedAssembly != "All Assemblies" && map.AssemblyName != _explorerSelectedAssembly)
                    continue;

                if (!string.IsNullOrEmpty(_explorerSearchQuery))
                {
                    bool matchSignal = map.SignalName.IndexOf(_explorerSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchCommand = map.CommandName.IndexOf(_explorerSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
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
                    row.style.backgroundColor = new StyleColor(new Color(0.18f, 0.22f, 0.28f));
                else
                    row.style.backgroundColor = new StyleColor(alternate ? new Color(0.15f, 0.15f, 0.17f) : new Color(0.18f, 0.18f, 0.2f));
                
                alternate = !alternate;

                // Select on click
                if (map.SignalType != null)
                {
                    row.RegisterCallback<MouseDownEvent>(evt =>
                    {
                        SelectSignalForTesting(map.SignalType);
                        evt.StopPropagation();
                    });
                }

                var l1 = new Label(map.SignalName) { style = { width = new Length(40, LengthUnit.Percent), color = new Color(0.7f, 0.85f, 1f), unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10 } };
                
                var handlerContainer = new VisualElement();
                handlerContainer.style.flexDirection = FlexDirection.Row;
                handlerContainer.style.alignItems = Align.Center;
                handlerContainer.style.width = new Length(40, LengthUnit.Percent);

                var l2 = new Label(map.CommandName) { style = { color = Color.white, fontSize = 10 } };
                handlerContainer.Add(l2);

                if (map.IsAsync)
                {
                    var badge = new Label("⚡ ASYNC")
                    {
                        style = {
                            fontSize = 7,
                            backgroundColor = new StyleColor(new Color(0.45f, 0.35f, 0.15f)),
                            color = new StyleColor(new Color(1f, 0.8f, 0.2f)),
                            paddingLeft = 2,
                            paddingRight = 2,
                            paddingTop = 1,
                            paddingBottom = 1,
                            marginLeft = 4,
                            borderTopLeftRadius = 2,
                            borderTopRightRadius = 2,
                            borderBottomLeftRadius = 2,
                            borderBottomRightRadius = 2,
                            unityFontStyleAndWeight = FontStyle.Bold
                        }
                    };
                    handlerContainer.Add(badge);
                }

                var l3 = new Label(map.Mode) { style = { width = new Length(20, LengthUnit.Percent), color = new Color(0.8f, 0.6f, 0.9f), fontSize = 9 } };

                row.Add(l1);
                row.Add(handlerContainer);
                row.Add(l3);
                _explorerScrollView.Add(row);
                _explorerRenderedRows.Add(row);
            }
        }

        private void SelectSignalForTesting(Type signalType)
        {
            _testerSelectedSignalType = signalType;
            _testerResultLog = null;
            try
            {
                _testerSignalInstance = Activator.CreateInstance(_testerSelectedSignalType);
                _testerSignalFields = _testerSelectedSignalType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                _testerSignalInstance = null;
                _testerSignalFields = Array.Empty<FieldInfo>();
                _testerResultLog = $"Create instance error: {ex.Message}";
                _testerResultColor = Color.red;
            }

            // Update row backgrounds
            FilterAndPopulateExplorerRows();
        }

        private void DrawSignalTesterIMGUI()
        {
            EnsureStyles();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Signal testing is only active in Play Mode. Select a signal on the left to prepare testing.", MessageType.Info);
                return;
            }

            if (_testerSelectedSignalType == null || _testerSignalInstance == null)
            {
                EditorGUILayout.HelpBox("Select a signal type from the list to test fire.", MessageType.Info);
                return;
            }

            GUILayout.Label($"Test Sinyali: {_testerSelectedSignalType.Name}", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            _testerScrollPos = EditorGUILayout.BeginScrollView(_testerScrollPos);

            for (int i = 0; i < _testerSignalFields.Length; i++)
            {
                var field = _testerSignalFields[i];
                object fieldValue = field.GetValue(_testerSignalInstance);
                object newValue = DrawTypedField(field.Name, fieldValue, field.FieldType);
                if (!Equals(fieldValue, newValue))
                {
                    field.SetValue(_testerSignalInstance, newValue);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(8);

            if (GUILayout.Button("Fire Test Signal ⚡", GUILayout.Height(30)))
            {
                FireSelectedSignal();
            }

            if (!string.IsNullOrEmpty(_testerResultLog))
            {
                EditorGUILayout.Space(5);
                var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
                style.normal.textColor = _testerResultColor;
                EditorGUILayout.LabelField(_testerResultLog, style);
            }
        }

        private void FireSelectedSignal()
        {
            try
            {
                var contexts = NexusRuntime.ActiveContexts;
                SignalBus targetBus = null;
                if (contexts != null)
                {
                    for (int i = 0; i < contexts.Count; i++)
                    {
                        if (contexts[i] is Context ctx && ctx.SignalBus != null)
                        {
                            targetBus = ctx.SignalBus as SignalBus;
                            break;
                        }
                    }
                }

                if (targetBus == null)
                {
                    _testerResultLog = "No active Context or SignalBus found.";
                    _testerResultColor = Color.yellow;
                    return;
                }

                var fireGeneric = typeof(SignalBus).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Fire" && m.IsGenericMethodDefinition);
                var fireMethod = fireGeneric.MakeGenericMethod(_testerSelectedSignalType);

                fireMethod.Invoke(targetBus, new[] { _testerSignalInstance });

                _testerResultLog = $"\u2713 Fired: {_testerSelectedSignalType.Name}";
                _testerResultColor = ButtonGreenColor;
            }
            catch (Exception ex)
            {
                _testerResultLog = $"Fire failed: {ex.Message}";
                _testerResultColor = Color.red;
                Debug.LogException(ex);
            }
        }
    }
}
