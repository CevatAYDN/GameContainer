using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public partial class NexusWindow
    {
        // ==========================================
        // ── TAB 2: CONTEXT WIZARD
        // ==========================================
        private void BuildWizardTab()
        {
            var toolbar = NexusEditorStyles.CreateToolbar("CONTEXT CREATION & UTILITIES WIZARD");
            _contentArea.Add(toolbar);

            var imguiView = new IMGUIContainer(DrawWizardIMGUI);
            imguiView.style.flexGrow = 1;
            imguiView.style.paddingLeft = 15;
            imguiView.style.paddingRight = 15;
            imguiView.style.paddingTop = 10;
            imguiView.style.paddingBottom = 10;
            _contentArea.Add(imguiView);
        }

        private void DrawWizardIMGUI()
        {
            EnsureStyles();

            _wizardSelectedSubTab = GUILayout.Toolbar(_wizardSelectedSubTab, _wizardSubTabNames);
            EditorGUILayout.Space(10);

            _wizardScroll = EditorGUILayout.BeginScrollView(_wizardScroll);

            switch (_wizardSelectedSubTab)
            {
                case 0:
                    DrawCreateRootTab();
                    break;
                case 1:
                    DrawViewMediatorGenTab();
                    break;
                case 2:
                    DrawCleanDeletionTab();
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCreateRootTab()
        {
            var manifest = GetCachedManifest();

            // Manifest generation
            GUILayout.Label("Bootstrap Manifest Generation", _headerStyle);
            EditorGUILayout.Space(5);

            if (manifest == null)
            {
                EditorGUILayout.HelpBox("No NexusBootstrapManifest found in the project. Create one to enable skeleton generation.", MessageType.Info);
                if (GUILayout.Button("Create Default Bootstrap Manifest", GUILayout.Height(25)))
                {
                    CreateDefaultManifest();
                }
            }
            else
            {
                EditorGUILayout.ObjectField("Active Manifest", manifest, typeof(NexusBootstrapManifest), false);
                EditorGUILayout.Space(5);

                EditorGUI.indentLevel = 1;
                EditorGUILayout.LabelField("Default Contexts:", string.Join(", ", manifest.DefaultContextNames));
                EditorGUILayout.Toggle("Generate Samples", manifest.GenerateSampleSignals || manifest.GenerateSampleCommands);
                EditorGUILayout.Toggle("Enable Inspector", manifest.EnableInspector);
                EditorGUI.indentLevel = 0;

                EditorGUILayout.Space(8);

                if (GUILayout.Button("Generate Skeleton from Manifest", _actionButtonStyle))
                {
                    GenerateSkeleton(manifest);
                }
            }

            EditorGUILayout.Space(15);
            DrawSeparator();
            EditorGUILayout.Space(15);

            // Custom Root Creation
            GUILayout.Label("Custom Root Context Creation", _headerStyle);
            EditorGUILayout.Space(5);

            EditorGUI.indentLevel = 1;
            _wizardContextName = EditorGUILayout.TextField("Context Name", _wizardContextName);
            _wizardScopeTag = EditorGUILayout.TextField("Scope Tag", _wizardScopeTag);

            // Parent Root Dropdown selection
            var sceneRoots = GetCachedSceneRoots();
            var rootNames = GetSceneRootNames(sceneRoots);
            int selectedIndex = 0;
            if (_wizardParentRoot != null)
            {
                for (int i = 0; i < sceneRoots.Length; i++)
                {
                    if (sceneRoots[i] == _wizardParentRoot)
                    {
                        selectedIndex = i + 1;
                        break;
                    }
                }
            }
            int newIndex = EditorGUILayout.Popup("Parent Root", selectedIndex, rootNames);
            if (newIndex == 0)
                _wizardParentRoot = null;
            else
                _wizardParentRoot = sceneRoots[newIndex - 1];

            // Assembly Scope Multi-select foldout
            EditorGUILayout.Space(5);
            _wizardAssembliesFoldout = EditorGUILayout.Foldout(_wizardAssembliesFoldout, $"Assembly Scopes ({_wizardSelectedAssemblies.Count} selected)");
            if (_wizardAssembliesFoldout)
            {
                EditorGUI.indentLevel++;
                var scrollHeight = Mathf.Min(_wizardAvailableAssemblies.Count * 20 + 5, 120);
                _wizardAssembliesScroll = EditorGUILayout.BeginScrollView(_wizardAssembliesScroll, GUILayout.Height(scrollHeight));
                foreach (var assemblyName in _wizardAvailableAssemblies)
                {
                    bool isSelected = _wizardSelectedAssemblies.Contains(assemblyName);
                    bool newSelected = EditorGUILayout.ToggleLeft(assemblyName, isSelected);
                    if (newSelected && !isSelected)
                        _wizardSelectedAssemblies.Add(assemblyName);
                    else if (!newSelected && isSelected)
                        _wizardSelectedAssemblies.Remove(assemblyName);
                }
                EditorGUILayout.EndScrollView();
                EditorGUI.indentLevel--;
            }

            // Lifecycle template toggle
            EditorGUILayout.Space(5);
            _wizardGenerateLifecycleScript = EditorGUILayout.Toggle("Create Lifecycle Template", _wizardGenerateLifecycleScript);

            EditorGUI.BeginDisabledGroup(!_wizardGenerateLifecycleScript);
            _wizardGenerateSampleArchitecture = EditorGUILayout.Toggle("Create Architecture Boilerplate", _wizardGenerateSampleArchitecture && _wizardGenerateLifecycleScript);
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel = 0;
            EditorGUILayout.Space(10);

            // Validation logic
            bool isValid = true;
            string validationError = "";

            if (string.IsNullOrWhiteSpace(_wizardContextName))
            {
                isValid = false;
                validationError = "Context Name cannot be empty.";
            }
            else if (_wizardContextName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                isValid = false;
                validationError = "Context Name contains invalid path characters.";
            }
            else if (string.IsNullOrWhiteSpace(_wizardScopeTag))
            {
                isValid = false;
                validationError = "Scope Tag cannot be empty.";
            }
            else
            {
                string path = $"Assets/Settings/{_wizardContextName}ContextData.asset";
                if (File.Exists(path))
                {
                    isValid = false;
                    validationError = $"A ContextData asset already exists at {path}. Use a different Context Name.";
                }
            }

            if (!isValid)
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!isValid);
            if (GUILayout.Button("Create Root & ContextData", _actionButtonStyle))
            {
                CreateRoot(_wizardContextName, _wizardScopeTag);
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawViewMediatorGenTab()
        {
            GUILayout.Label("Generate View & Mediator", _headerStyle);
            EditorGUILayout.Space(5);

            _wizardViewName = EditorGUILayout.TextField("View Name", _wizardViewName);

            var sceneRoots = GetCachedSceneRoots();
            var rootNames = new string[sceneRoots.Length];
            int selectedIndex = 0;
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                rootNames[i] = sceneRoots[i].gameObject.name;
                if (sceneRoots[i] == _wizardViewTargetRoot)
                    selectedIndex = i;
            }

            if (sceneRoots.Length == 0)
            {
                EditorGUILayout.HelpBox("No active Roots found in scene. Create a Root first.", MessageType.Warning);
                return;
            }

            int newIndex = EditorGUILayout.Popup("Target Root Context", selectedIndex, rootNames);
            _wizardViewTargetRoot = sceneRoots[newIndex];

            _wizardCreateViewGo = EditorGUILayout.Toggle("Create GameObject in Scene", _wizardCreateViewGo);

            EditorGUILayout.Space(10);

            bool isValid = !string.IsNullOrWhiteSpace(_wizardViewName);
            if (!isValid)
            {
                EditorGUILayout.HelpBox("View Name cannot be empty.", MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!isValid);
            if (GUILayout.Button("Generate View & Mediator Files", GUILayout.Height(25)))
            {
                GenerateViewAndMediator(_wizardViewName, _wizardViewTargetRoot, _wizardCreateViewGo);
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawCleanDeletionTab()
        {
            GUILayout.Label("Clean Deletion Tool", _headerStyle);
            EditorGUILayout.Space(5);

            var sceneRoots = GetCachedSceneRoots();
            if (sceneRoots.Length == 0)
            {
                EditorGUILayout.HelpBox("No active Roots found in scene.", MessageType.Info);
                return;
            }

            var rootNames = new string[sceneRoots.Length];
            int selectedIndex = 0;
            for (int i = 0; i < sceneRoots.Length; i++)
            {
                rootNames[i] = sceneRoots[i].gameObject.name;
                if (sceneRoots[i] == _wizardRootToDelete)
                    selectedIndex = i;
            }

            int newIndex = EditorGUILayout.Popup("Root Context to Delete", selectedIndex, rootNames);
            _wizardRootToDelete = sceneRoots[newIndex];

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("WARNING: This will permanently delete:\n" +
                                    "- The Root GameObject from the active scene.\n" +
                                    "- The associated ContextData ScriptableObject.\n" +
                                    "- The generated script directory Assets/Scripts/Nexus/<ContextName>/\n\n" +
                                    "Make sure you have backed up your custom script changes before committing!", MessageType.Warning);

            if (GUILayout.Button("DELETE ROOT & ALL RELATED ASSETS", _deleteButtonStyle))
            {
                string contextName = _wizardRootToDelete.ContextData != null ? _wizardRootToDelete.ContextData.name.Replace("ContextData", "") : _wizardRootToDelete.gameObject.name.Replace("Root", "");
                if (EditorUtility.DisplayDialog("Confirm Clean Deletion", 
                    $"Are you absolutely sure you want to delete context '{contextName}' and all its assets/GameObjects? This action cannot be fully undone.", 
                    "Yes, Delete", "Cancel"))
                {
                    DeleteRootContext(_wizardRootToDelete);
                }
            }
        }

        private void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.25f, 0.25f, 0.28f));
        }

        // ==========================================
        // ── WIZARD CORE HELPERS
        // ==========================================
        private Root[] GetCachedSceneRoots()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cachedSceneRoots == null || now - _lastRootCacheTime > RootCacheDuration)
            {
                _cachedSceneRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
                _lastRootCacheTime = now;
            }
            return _cachedSceneRoots;
        }

        private NexusBootstrapManifest GetCachedManifest()
        {
            if (!_manifestCacheValid)
            {
                _cachedManifest = FindBootstrapManifest();
                _manifestCacheValid = true;
            }
            return _cachedManifest;
        }

        private void InvalidateCaches()
        {
            _cachedSceneRoots = null;
            _manifestCacheValid = false;
        }

        private void PopulateAssemblies()
        {
            _wizardAvailableAssemblies.Clear();
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || 
                    name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") || name.StartsWith("nunit") || 
                    name.Contains("PlayerLoop") || name.Contains("JetBrains"))
                {
                    continue;
                }
                if (!_wizardAvailableAssemblies.Contains(name))
                    _wizardAvailableAssemblies.Add(name);
            }
            _wizardAvailableAssemblies.Sort();

            if (_wizardAvailableAssemblies.Contains("Assembly-CSharp"))
                _wizardSelectedAssemblies.Add("Assembly-CSharp");
        }

        private NexusBootstrapManifest FindBootstrapManifest()
        {
            var guids = AssetDatabase.FindAssets("t:NexusBootstrapManifest");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<NexusBootstrapManifest>(path);
            }
            return null;
        }

        private void CreateDefaultManifest()
        {
            var manifest = ScriptableObject.CreateInstance<NexusBootstrapManifest>();
            manifest.DefaultContextNames = new string[] { "Global", "Gameplay", "UI" };
            manifest.GenerateSampleSignals = true;
            manifest.GenerateSampleCommands = true;
            manifest.EnableInspector = true;

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
                AssetDatabase.CreateFolder("Assets", "Settings");

            string path = "Assets/Settings/NexusBootstrapManifest.asset";
            AssetDatabase.CreateAsset(manifest, path);
            AssetDatabase.SaveAssets();

            _manifestCacheValid = false;
            Debug.Log($"[Nexus] Created default Bootstrap Manifest at {path}");
            AssetDatabase.Refresh();
        }

        private void GenerateSkeleton(NexusBootstrapManifest manifest)
        {
            foreach (var name in manifest.DefaultContextNames)
            {
                CreateRoot(name, name);
            }

            if (manifest.GenerateSampleSignals || manifest.GenerateSampleCommands)
            {
                string samplesDir = "Assets/Samples/Nexus";
                if (!Directory.Exists(samplesDir))
                    Directory.CreateDirectory(samplesDir);

                if (manifest.GenerateSampleSignals)
                {
                    string signalPath = Path.Combine(samplesDir, "SampleSignals.cs");
                    File.WriteAllText(signalPath, GetSampleSignalCode());
                }

                if (manifest.GenerateSampleCommands)
                {
                    string commandPath = Path.Combine(samplesDir, "SampleCommands.cs");
                    File.WriteAllText(commandPath, GetSampleCommandCode());
                }

                InvalidateCaches();
                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Nexus Setup", "Skeleton generated successfully!", "OK");
        }

        private void CreateRoot(string contextName, string scopeTag)
        {
            var go = new GameObject($"{contextName}Root");
            var root = go.AddComponent<Root>();
            if (_wizardParentRoot != null)
                go.transform.SetParent(_wizardParentRoot.transform);

            var contextData = ScriptableObject.CreateInstance<ContextData>();
            contextData.ScopeTag = scopeTag;
            contextData.AssemblyScopes = new List<string>(_wizardSelectedAssemblies).ToArray();

            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
                AssetDatabase.CreateFolder("Assets", "Settings");

            string path = $"Assets/Settings/{contextName}ContextData.asset";
            AssetDatabase.CreateAsset(contextData, path);
            AssetDatabase.SaveAssets();

            var serializedRoot = new SerializedObject(root);
            var contextDataProp = serializedRoot.FindProperty("contextData");
            if (contextDataProp != null)
                contextDataProp.objectReferenceValue = contextData;

            if (_wizardParentRoot != null)
            {
                var parentProp = serializedRoot.FindProperty("parentRoot");
                if (parentProp != null) parentProp.objectReferenceValue = _wizardParentRoot;
            }
            serializedRoot.ApplyModifiedProperties();

            if (_wizardGenerateLifecycleScript)
            {
                string scriptsDir = "Assets/Scripts/Nexus";
                if (!Directory.Exists(scriptsDir))
                    Directory.CreateDirectory(scriptsDir);
                
                string scriptPath;
                if (_wizardGenerateSampleArchitecture)
                {
                    string contextDir = Path.Combine(scriptsDir, contextName);
                    string signalsDir = Path.Combine(contextDir, "Signals");
                    string modelsDir = Path.Combine(contextDir, "Models");
                    string commandsDir = Path.Combine(contextDir, "Commands");
                    string viewsDir = Path.Combine(contextDir, "Views");

                    Directory.CreateDirectory(contextDir);
                    Directory.CreateDirectory(signalsDir);
                    Directory.CreateDirectory(modelsDir);
                    Directory.CreateDirectory(commandsDir);
                    Directory.CreateDirectory(viewsDir);

                    File.WriteAllText(Path.Combine(signalsDir, $"{contextName}Signals.cs"), GetSignalsBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"I{contextName}Model.cs"), GetModelInterfaceBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"{contextName}Model.cs"), GetModelImplementationBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(commandsDir, $"{contextName}Command.cs"), GetCommandBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}View.cs"), GetViewBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}Mediator.cs"), GetMediatorBoilerplate(contextName));

                    scriptPath = Path.Combine(contextDir, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, GetLifecycleBoilerplateWithBindings(contextName));
                }
                else
                {
                    scriptPath = Path.Combine(scriptsDir, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, GetLifecycleTemplateCode(contextName));
                }
                Debug.Log($"[Nexus] Generated lifecycle template at {scriptPath}");
            }

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");

            InvalidateCaches();
            AssetDatabase.Refresh();

            ShowPostCreationGuide(go.name, contextName, scopeTag);
        }

        private void ShowPostCreationGuide(string goName, string contextName, string scopeTag)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                $"Root Created: {goName}",
                $"Successfully created {goName} (ScopeTag: {scopeTag}).\n\n" +
                "--- NEXT STEPS ---\n\n" +
                "1. Sinyalleri İzleme  → Live Tracer sekmesini açın.\n" +
                "2. Mimarileri Tanımla → Lifecycle ve Command sınıflarını doldurun.\n" +
                "3. Play Mode'a Geçin   → Sinyal akışını canlı gözlemleyin.",
                "Sinyal Takibine Git",
                "Signal Explorer'a Git",
                "Tamam"
            );

            if (choice == 0)
                SwitchTab(TabType.Tracer);
            else if (choice == 1)
                SwitchTab(TabType.Explorer);
        }

        private void GenerateViewAndMediator(string viewName, Root targetRoot, bool createGo)
        {
            string contextName = targetRoot.ContextData != null ? targetRoot.ContextData.name.Replace("ContextData", "") : targetRoot.gameObject.name.Replace("Root", "");
            string viewsDir = $"Assets/Scripts/Nexus/{contextName}/Views";

            try
            {
                if (!Directory.Exists(viewsDir))
                    Directory.CreateDirectory(viewsDir);

                string viewPath = Path.Combine(viewsDir, $"{viewName}View.cs");
                string mediatorPath = Path.Combine(viewsDir, $"{viewName}Mediator.cs");

                if (File.Exists(viewPath) || File.Exists(mediatorPath))
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Files?", $"Files for {viewName}View already exist. Do you want to overwrite them?", "Yes", "No"))
                        return;
                }

                File.WriteAllText(viewPath, GetGenericViewBoilerplate(viewName, contextName));
                File.WriteAllText(mediatorPath, GetGenericMediatorBoilerplate(viewName, contextName));

                if (createGo)
                {
                    EditorPrefs.SetString("com.nexus.core.PendingViewName", viewName);
                    EditorPrefs.SetString("com.nexus.core.PendingViewRootName", targetRoot.gameObject.name);
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Generated successfully", $"Successfully generated {viewName}View and {viewName}Mediator under {viewsDir}.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] View/Mediator generation failed: {ex.Message}");
            }
        }

        private void DeleteRootContext(Root root)
        {
            if (root == null) return;
            try
            {
                var go = root.gameObject;
                string contextName = root.ContextData != null ? root.ContextData.name.Replace("ContextData", "") : go.name.Replace("Root", "");

                if (root.ContextData != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(root.ContextData);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                }

                string scriptsDir = $"Assets/Scripts/Nexus/{contextName}";
                if (AssetDatabase.IsValidFolder(scriptsDir))
                    AssetDatabase.DeleteAsset(scriptsDir);
                else
                {
                    string flatScriptPath = $"Assets/Scripts/Nexus/{contextName}Lifecycle.cs";
                    if (File.Exists(flatScriptPath)) AssetDatabase.DeleteAsset(flatScriptPath);
                }

                Undo.DestroyObjectImmediate(go);
                InvalidateCaches();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Root Deleted", $"Successfully deleted context '{contextName}' and its related assets.", "OK");
                
                // FIXED CS0019 compilation error by checking root.Context
                if (_selectedContextForInspector == root.Context)
                    _selectedContextForInspector = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Failed to cleanly delete root: {ex.Message}");
            }
        }

        private string[] GetSceneRootNames(Root[] roots)
        {
            var names = new string[roots.Length + 1];
            names[0] = "None (Root Context)";
            for (int i = 0; i < roots.Length; i++)
            {
                names[i + 1] = roots[i].gameObject.name;
            }
            return names;
        }

        private static int CountSceneRoots()
        {
            var roots = UnityEngine.Object.FindObjectsByType<Root>();
            return roots?.Length ?? 0;
        }
    }
}
