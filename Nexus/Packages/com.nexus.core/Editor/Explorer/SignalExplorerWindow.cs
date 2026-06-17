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

            var titleLabel = new Label("SIGNAL-COMMAND STATIC MAP");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleLabel.style.marginRight = 20;
            toolbar.Add(titleLabel);

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
            _scrollView.Clear();
            _renderedRows.Clear();

            // Find all classes in active assemblies with SignalHandler attributes
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var mappings = new List<MappingInfo>();

            foreach (var assembly in assemblies)
            {
                // Simple filter to skip heavy system DLLs
                var assemblyName = assembly.GetName().Name;
                if (assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib") || assemblyName.StartsWith("Mono") || assemblyName.StartsWith("UnityEditor") && !assemblyName.Contains("com.nexus"))
                {
                    continue;
                }

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract)
                        {
                            var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                            foreach (var attr in attrs)
                            {
                                mappings.Add(new MappingInfo(
                                    attr.SignalType.Name,
                                    type.Name,
                                    attr.Mode.ToString(),
                                    attr.Priority.ToString()
                                ));
                            }

                            var compositeAttr = type.GetCustomAttribute<CompositeSignalHandlerAttribute>();
                            if (compositeAttr != null)
                            {
                                var sigs = new List<string>();
                                foreach (var s in compositeAttr.SignalTypes) sigs.Add(s.Name);
                                string compositeSigs = $"Composite({string.Join(" + ", sigs)})";

                                mappings.Add(new MappingInfo(
                                    compositeSigs,
                                    type.Name,
                                    compositeAttr.OneShot ? "Composite (OneShot)" : "Composite (Re-trigger)",
                                    compositeAttr.Priority.ToString()
                                ));
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore unloadable assemblies
                }
            }

            // Sort mappings by Signal name
            mappings.Sort((a, b) => string.Compare(a.SignalName, b.SignalName, StringComparison.OrdinalIgnoreCase));

            if (mappings.Count == 0)
            {
                var noItems = new Label("No SignalHandlers found in active assemblies.") { style = { color = Color.gray, alignSelf = Align.Center, marginTop = 20 } };
                _scrollView.Add(noItems);
                return;
            }

            // Populate rows
            bool alternate = false;
            foreach (var map in mappings)
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
                var l2 = new Label(map.CommandName) { style = { width = new Length(35, LengthUnit.Percent), color = Color.white, fontSize = 11 } };
                
                var l3 = new Label(map.Mode) { style = { width = new Length(18, LengthUnit.Percent), color = new Color(0.8f, 0.6f, 0.9f), fontSize = 10 } };
                var l4 = new Label(map.Priority) { style = { width = new Length(12, LengthUnit.Percent), color = new Color(1f, 0.8f, 0.4f), unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } };

                row.Add(l1);
                row.Add(l2);
                row.Add(l3);
                row.Add(l4);
                _scrollView.Add(row);
            }
        }

        private struct MappingInfo
        {
            public string SignalName;
            public string CommandName;
            public string Mode;
            public string Priority;

            public MappingInfo(string signalName, string commandName, string mode, string priority)
            {
                SignalName = signalName;
                CommandName = commandName;
                Mode = mode;
                Priority = priority;
            }
        }
    }
}
