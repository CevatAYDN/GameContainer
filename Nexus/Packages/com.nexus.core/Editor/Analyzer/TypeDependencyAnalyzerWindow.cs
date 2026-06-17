using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Nexus.Core;

namespace Nexus.Editor
{
    public class TypeDependencyAnalyzerWindow : EditorWindow
    {
        private class AnalysisResult
        {
            public Type Type;
            public List<DependentInfo> Dependents = new();
        }

        private class DependentInfo
        {
            public string OwnerType;
            public string MemberName;
        }

        private class InjectEntry
        {
            public Type TargetType;
            public string Member;
        }

        private TextField _searchField;
        private ScrollView _scrollView;
        private string _searchedTypeName = "PlayerModel";

        // Cache: type name (lower) → cached analysis
        private static readonly ConcurrentDictionary<string, AnalysisResult> s_analysisCache = new();
        private static bool s_assemblyCacheDirty = true;
        // Index: source type name → list of (target type, member desc)
        private static readonly ConcurrentDictionary<string, List<InjectEntry>> s_injectTargetIndex = new();

        [MenuItem("Window/Nexus/Type Analyzer")]
        public static void ShowWindow()
        {
            var window = GetWindow<TypeDependencyAnalyzerWindow>("Nexus Type Analyzer");
            window.minSize = new Vector2(400, 450);
            window.Show();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_assemblyCacheDirty = true;
            s_analysisCache.Clear();
            s_injectTargetIndex.Clear();
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

            var titleLabel = new Label("TYPE COUPLING ANALYZER");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            titleLabel.style.marginRight = 20;
            toolbar.Add(titleLabel);

            _searchField = new TextField("Type Name") { value = _searchedTypeName };
            _searchField.style.flexGrow = 1;
            _searchField.style.color = Color.white;
            toolbar.Add(_searchField);

            var analyzeButton = new Button(AnalyzeType) { text = "Analyze" };
            analyzeButton.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            analyzeButton.style.borderTopLeftRadius = 4;
            analyzeButton.style.borderTopRightRadius = 4;
            analyzeButton.style.borderBottomLeftRadius = 4;
            analyzeButton.style.borderBottomRightRadius = 4;
            analyzeButton.style.color = Color.white;
            analyzeButton.style.marginLeft = 10;
            toolbar.Add(analyzeButton);

            root.Add(toolbar);

            // Scrollview
            _scrollView = new ScrollView();
            _scrollView.style.flexGrow = 1;
            _scrollView.style.paddingLeft = 15;
            _scrollView.style.paddingRight = 15;
            _scrollView.style.paddingTop = 15;
            _scrollView.style.paddingBottom = 15;
            root.Add(_scrollView);

            AnalyzeType();
        }

        private void AnalyzeType()
        {
            _scrollView.Clear();
            _searchedTypeName = _searchField?.value ?? "";
            if (string.IsNullOrEmpty(_searchedTypeName))
            {
                var label = new Label("Please enter a type name to analyze.") { style = { color = Color.gray, alignSelf = Align.Center, marginTop = 20 } };
                _scrollView.Add(label);
                return;
            }

            // Find type (cache the lookup to avoid re-scanning assembly chain)
            string cacheKey = _searchedTypeName.ToLowerInvariant();
            if (!s_analysisCache.TryGetValue(cacheKey, out var cached))
            {
                Type targetType = null;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    var types = assembly.GetTypes();
                    foreach (var t in types)
                    {
                        if (t.Name.Equals(_searchedTypeName, StringComparison.OrdinalIgnoreCase) || t.FullName.Equals(_searchedTypeName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetType = t;
                            break;
                        }
                    }
                    if (targetType != null) break;
                }

                if (targetType == null)
                {
                    var label = new Label($"Could not find type '{_searchedTypeName}' in active assemblies.") { style = { color = new StyleColor(new Color(1f, 0.4f, 0.4f)), alignSelf = Align.Center, marginTop = 20 } };
                    _scrollView.Add(label);
                    return;
                }

                // Build dependents index on first use after script reload
                EnsureInjectIndexBuilt();

                // Find dependents for this type
                var result = new AnalysisResult { Type = targetType };
                string searchName = targetType.FullName;
                foreach (var kvp in s_injectTargetIndex)
                {
                    foreach (var entry in kvp.Value)
                    {
                        if (entry.TargetType == targetType || entry.TargetType.FullName == searchName)
                        {
                            result.Dependents.Add(new DependentInfo { OwnerType = kvp.Key, MemberName = entry.Member });
                        }
                    }
                }

                cached = result;
                s_analysisCache[cacheKey] = cached;
            }

            // Header for selected type
            var selectedTypeHeader = new Label(cached.Type.FullName);
            selectedTypeHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            selectedTypeHeader.style.fontSize = 14;
            selectedTypeHeader.style.color = Color.white;
            selectedTypeHeader.style.borderBottomWidth = 1;
            selectedTypeHeader.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            selectedTypeHeader.style.paddingBottom = 6;
            _scrollView.Add(selectedTypeHeader);

            // 1. Dependencies Section (What this type requires)
            RenderDependenciesSection(cached.Type);

            // 2. Dependents Section (Who depends on this type)
            RenderDependentsSection(cached.Dependents, cached.Type);
        }

        private void EnsureInjectIndexBuilt()
        {
            if (!s_assemblyCacheDirty) return;
            s_assemblyCacheDirty = false;
            s_injectTargetIndex.Clear();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        // Scan [Inject] fields
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var f in fields)
                        {
                            if (f.GetCustomAttribute<InjectAttribute>() != null)
                            {
                                AddToIndex(type, f.FieldType, $"Field: {f.Name}");
                            }
                        }

                        // Scan [Inject] properties
                        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var p in properties)
                        {
                            if (p.GetCustomAttribute<InjectAttribute>() != null)
                            {
                                AddToIndex(type, p.PropertyType, $"Property: {p.Name}");
                            }
                        }

                        // Scan constructor parameters
                        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                        foreach (var ctor in ctors)
                        {
                            foreach (var p in ctor.GetParameters())
                            {
                                AddToIndex(type, p.ParameterType, $"Constructor: {p.Name}");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void AddToIndex(Type sourceType, Type targetType, string memberDesc)
        {
            string key = sourceType.FullName ?? sourceType.Name;
            if (!s_injectTargetIndex.ContainsKey(key))
                s_injectTargetIndex[key] = new List<InjectEntry>();
            s_injectTargetIndex[key].Add(new InjectEntry { TargetType = targetType, Member = memberDesc });
        }

        private void RenderDependenciesSection(Type type)
        {
            var section = new VisualElement();
            section.style.marginTop = 15;

            var title = new Label("Dependencies (Required Injections):");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(new Color(0.3f, 0.8f, 1f));
            title.style.fontSize = 11;
            section.Add(title);

            var list = new VisualElement();
            list.style.marginLeft = 10;
            list.style.marginTop = 5;

            int count = 0;

            // Fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.GetCustomAttribute<InjectAttribute>() != null)
                {
                    list.Add(new Label($"• Field: {f.Name} ({f.FieldType.Name})") { style = { color = Color.white, fontSize = 11 } });
                    count++;
                }
            }

            // Properties
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var p in properties)
            {
                if (p.GetCustomAttribute<InjectAttribute>() != null)
                {
                    list.Add(new Label($"• Property: {p.Name} ({p.PropertyType.Name})") { style = { color = Color.white, fontSize = 11 } });
                    count++;
                }
            }

            // Methods
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.GetCustomAttribute<InjectAttribute>() != null)
                {
                    var paramNames = new List<string>();
                    foreach (var p in m.GetParameters()) paramNames.Add($"{p.ParameterType.Name} {p.Name}");
                    list.Add(new Label($"• Method: {m.Name}({string.Join(", ", paramNames)})") { style = { color = Color.white, fontSize = 11 } });
                    count++;
                }
            }

            if (count == 0)
            {
                list.Add(new Label("No [Inject] dependencies found.") { style = { color = Color.gray, fontSize = 10 } });
            }

            section.Add(list);
            _scrollView.Add(section);
        }

        private void RenderDependentsSection(List<DependentInfo> dependents, Type targetType)
        {
            var section = new VisualElement();
            section.style.marginTop = 20;

            var title = new Label($"Referenced By (Dependents): {dependents.Count}");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(new Color(0.9f, 0.6f, 1f));
            title.style.fontSize = 11;
            section.Add(title);

            var list = new VisualElement();
            list.style.marginLeft = 10;
            list.style.marginTop = 5;

            if (dependents.Count == 0)
            {
                list.Add(new Label("No other types are injecting this type.") { style = { color = Color.gray, fontSize = 10 } });
            }
            else
            {
                foreach (var dep in dependents)
                {
                    list.Add(new Label($"• {dep.OwnerType} ({dep.MemberName})") { style = { color = Color.white, fontSize = 11 } });
                }
            }

            section.Add(list);
            _scrollView.Add(section);
        }
    }
}
