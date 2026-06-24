using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class TypeAnalyzerPlugin : NexusEditorPlugin
    {
        public override string Id => "TypeAnalyzer";
        public override string DisplayName => "Type Analyzer";
        public override int Order => 5;

        private TextField _searchField;
        private ScrollView _scrollView;
        private string _searchedTypeName = "PlayerModel";

        // Cache: type name (lower) → cached analysis
        private static readonly ConcurrentDictionary<string, AnalysisResult> s_analysisCache = new();
        private static bool s_assemblyCacheDirty = true;
        
        // Index: source type name → list of (target type, member desc)
        private static readonly ConcurrentDictionary<string, List<InjectEntry>> s_injectTargetIndex = new();

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

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            s_assemblyCacheDirty = true;
            s_analysisCache.Clear();
            s_injectTargetIndex.Clear();
        }

        public override VisualElement CreateView()
        {
            var view = new VisualElement { style = { flexGrow = 1 } };

            // Toolbar
            var toolbar = NexusEditorStyles.CreateToolbar("TYPE COUPLING ANALYZER");

            _searchField = new TextField("Type Name") { value = _searchedTypeName, style = { flexGrow = 1, color = Color.white } };
            toolbar.Add(_searchField);

            var analyzeButton = NexusEditorStyles.CreateButton("Analyze", AnalyzeType, NexusEditorStyles.BtnGray);
            analyzeButton.style.marginLeft = 10;
            toolbar.Add(analyzeButton);

            view.Add(toolbar);

            // Scrollview
            _scrollView = new ScrollView
            {
                style =
                {
                    flexGrow = 1,
                    paddingLeft = 15,
                    paddingRight = 15,
                    paddingTop = 15,
                    paddingBottom = 15
                }
            };
            view.Add(_scrollView);

            AnalyzeType();

            return view;
        }

        private void AnalyzeType()
        {
            if (_scrollView == null) return;
            _scrollView.Clear();
            
            _searchedTypeName = _searchField?.value ?? _searchedTypeName;
            if (string.IsNullOrEmpty(_searchedTypeName))
            {
                    var label = NexusEditorStyles.CreateEmptyState("Please enter a type name to analyze.");
                _scrollView.Add(label);
                return;
            }

            string cacheKey = _searchedTypeName.ToLowerInvariant();
            if (!s_analysisCache.TryGetValue(cacheKey, out var cached))
            {
                Type targetType = null;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    var assemblyName = assembly.GetName().Name;
                    if (assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib") || assemblyName.StartsWith("Mono"))
                        continue;

                    try
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
                    }
                    catch (ReflectionTypeLoadException) { }
                    if (targetType != null) break;
                }

                if (targetType == null)
                {
                    var label = new Label($"Could not find type '{_searchedTypeName}' in active assemblies.") { style = { color = new StyleColor(NexusEditorStyles.AccentRed), alignSelf = Align.Center, marginTop = 20 } };
                    _scrollView.Add(label);
                    return;
                }

                EnsureInjectIndexBuilt();

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
            selectedTypeHeader.style.borderBottomColor = new StyleColor(NexusEditorStyles.BorderLight);
            selectedTypeHeader.style.paddingBottom = 6;
            _scrollView.Add(selectedTypeHeader);

            // Dependencies Section
            RenderDependenciesSection(cached.Type);

            // Dependents Section
            RenderDependentsSection(cached.Dependents);
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
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Nexus] Failed to scan assembly '{assembly.GetName().Name}' for dependency analysis: {ex.Message}");
                }
            }
        }

        private static void AddToIndex(Type sourceType, Type targetType, string memberDesc)
        {
            string key = sourceType.FullName ?? sourceType.Name;
            var list = s_injectTargetIndex.GetOrAdd(key, _ => new List<InjectEntry>());
            list.Add(new InjectEntry { TargetType = targetType, Member = memberDesc });
        }

        private void RenderDependenciesSection(Type type)
        {
            var section = new VisualElement { style = { marginTop = 15 } };

            var title = new Label("Dependencies (Required Injections):");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
            title.style.fontSize = 11;
            title.style.marginBottom = 4;
            section.Add(title);

            var list = new VisualElement { style = { marginLeft = 10, marginTop = 5 } };
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

        private void RenderDependentsSection(List<DependentInfo> dependents)
        {
            var section = new VisualElement { style = { marginTop = 20 } };

            var title = new Label($"Referenced By (Dependents): {dependents.Count}");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(NexusEditorStyles.AccentPurple);
            title.style.fontSize = 11;
            section.Add(title);

            var list = new VisualElement { style = { marginLeft = 10, marginTop = 5 } };

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
