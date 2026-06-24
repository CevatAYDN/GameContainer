using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class WizardPlugin : NexusEditorPlugin
    {
        public override string Id => "Wizard";
        public override string DisplayName => "Context Wizard";
        public override int Order => 1;

        private enum SubTab
        {
            CreateRoot = 0,
            ViewMediatorGen = 1,
            CleanDeletion = 2
        }

        // Custom path inputs
        private string _wizardScriptsPath = "Assets/Scripts/Nexus";
        private string _wizardSettingsPath = "Assets/Settings";

        // Inputs for Custom Root Context Creation
        private string _wizardContextName = "Gameplay";
        private string _wizardScopeTag = "Gameplay";
        private string _wizardParentRootName = "None (Root Context)";
        private readonly HashSet<string> _wizardSelectedAssemblies = new();
        private bool _wizardGenerateLifecycleScript = true;
        private bool _wizardGenerateSampleArchitecture = true;

        // Inputs for View/Mediator Gen
        private string _wizardViewName = "GameplayHUD";
        private string _wizardViewTargetRootName = "";
        private bool _wizardCreateViewGo = true;

        // Inputs for Clean Deletion
        private string _wizardRootToDeleteName = "";

        // UI Element References
        private VisualElement _contentRoot;
        private VisualElement _subTabContent;
        private Label _validationLabel;
        private Button _createRootButton;
        
        private DropdownField _parentRootDropdown;
        private DropdownField _viewTargetRootDropdown;
        private DropdownField _deleteRootDropdown;

        private List<string> _wizardAvailableAssemblies = new();
        private Root[] _cachedSceneRoots = Array.Empty<Root>();

        private SubTab _selectedSubTab = SubTab.CreateRoot;

        public override VisualElement CreateView()
        {
            _contentRoot = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("CONTEXT CREATION & UTILITIES WIZARD");
            _contentRoot.Add(toolbar);

            // Tab navigation buttons
            var tabHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) } };
            
            var btnCreateRoot = CreateSubTabButton("Create Root", SubTab.CreateRoot);
            var btnViewGen = CreateSubTabButton("View/Mediator Gen", SubTab.ViewMediatorGen);
            var btnDelete = CreateSubTabButton("Clean Deletion", SubTab.CleanDeletion);

            tabHeader.Add(btnCreateRoot);
            tabHeader.Add(btnViewGen);
            tabHeader.Add(btnDelete);
            _contentRoot.Add(tabHeader);

            _subTabContent = new ScrollView { style = { flexGrow = 1, paddingLeft = 15, paddingRight = 15, paddingTop = 15, paddingBottom = 15 } };
            _contentRoot.Add(_subTabContent);

            PopulateAvailableAssemblies();
            RefreshSceneRoots();
            RenderSubTab();

            // Hook scene hierarchy changes to dynamically update dropdowns
            EditorApplication.hierarchyChanged += OnHierarchyChanged;

            return _contentRoot;
        }

        public override void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        private void OnHierarchyChanged()
        {
            RefreshSceneRoots();
            UpdateDropdownChoices();
        }

        private Button CreateSubTabButton(string label, SubTab tab)
        {
            var btn = new Button(() =>
            {
                _selectedSubTab = tab;
                HighlightActiveSubTab();
                RenderSubTab();
            }) { text = label };

            btn.name = $"SubTab_{(int)tab}";
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

        private void HighlightActiveSubTab()
        {
            if (_contentRoot == null) return;
            foreach (SubTab tab in Enum.GetValues(typeof(SubTab)))
            {
                int idx = (int)tab;
                var btn = _contentRoot.Q<Button>($"SubTab_{idx}");
                if (btn != null)
                {
                    if (tab == _selectedSubTab)
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
        }

        private void RenderSubTab()
        {
            if (_subTabContent == null) return;
            _subTabContent.Clear();
            HighlightActiveSubTab();

            switch (_selectedSubTab)
            {
                case SubTab.CreateRoot:
                    BuildCreateRootTab();
                    break;
                case SubTab.ViewMediatorGen:
                    BuildViewMediatorGenTab();
                    break;
                case SubTab.CleanDeletion:
                    BuildCleanDeletionTab();
                    break;
            }
        }

        private void BuildCreateRootTab()
        {
            var manifest = FindBootstrapManifest();

            // Section 1: Manifest Generation
            var manifestGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, "BOOTSTRAP MANIFEST GENERATION");
            if (manifest == null)
            {
                var hint = NexusEditorStyles.CreateHint("No NexusBootstrapManifest found in the project. Create one to enable skeleton generation.");
                manifestGroup.Add(hint);

                var createBtn = NexusEditorStyles.CreateButton("Create Default Bootstrap Manifest", CreateDefaultManifest, NexusEditorStyles.BtnBlue);
                manifestGroup.Add(createBtn);
            }
            else
            {
                var details = new VisualElement { style = { paddingLeft = 10, marginTop = 4 } };
                details.Add(new Label($"Active Manifest: {manifest.name}") { style = { color = Color.white, fontSize = 10 } });
                details.Add(new Label($"Default Contexts: {string.Join(", ", manifest.DefaultContextNames)}") { style = { color = NexusEditorStyles.TextSecondary, fontSize = 9 } });
                manifestGroup.Add(details);

                var genBtn = NexusEditorStyles.CreateButton("Generate Skeleton from Manifest", () => GenerateSkeleton(manifest), NexusEditorStyles.BtnBlue);
                manifestGroup.Add(genBtn);
            }

            // Section 2: Custom Root Creation
            var creationGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, "CUSTOM ROOT CONTEXT CREATION");

            // Input Fields
            var contextNameField = new TextField("Context Name") { value = _wizardContextName };
            contextNameField.RegisterValueChangedCallback(evt => { _wizardContextName = evt.newValue; ValidateCreateRootForm(); });
            creationGroup.Add(contextNameField);

            var scopeTagField = new TextField("Scope Tag") { value = _wizardScopeTag };
            scopeTagField.RegisterValueChangedCallback(evt => { _wizardScopeTag = evt.newValue; ValidateCreateRootForm(); });
            creationGroup.Add(scopeTagField);

            // Path Configuration
            var pathsGroup = new VisualElement { style = { marginTop = 5, borderTopWidth = 1, borderTopColor = new StyleColor(NexusEditorStyles.BorderColor), paddingTop = 5 } };
            pathsGroup.Add(new Label("Paths Configuration") { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginBottom = 4 } });

            var scriptsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var scriptsPathField = new TextField("Scripts Folder") { value = _wizardScriptsPath, style = { flexGrow = 1 } };
            scriptsPathField.RegisterValueChangedCallback(evt => { _wizardScriptsPath = evt.newValue; ValidateCreateRootForm(); });
            scriptsPathRow.Add(scriptsPathField);
            var browseScriptsBtn = new Button(() => BrowseFolder(path => { scriptsPathField.value = path; })) { text = "Browse" };
            scriptsPathRow.Add(browseScriptsBtn);
            pathsGroup.Add(scriptsPathRow);

            var settingsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
            var settingsPathField = new TextField("Settings Folder") { value = _wizardSettingsPath, style = { flexGrow = 1 } };
            settingsPathField.RegisterValueChangedCallback(evt => { _wizardSettingsPath = evt.newValue; ValidateCreateRootForm(); });
            settingsPathRow.Add(settingsPathField);
            var browseSettingsBtn = new Button(() => BrowseFolder(path => { settingsPathField.value = path; })) { text = "Browse" };
            settingsPathRow.Add(browseSettingsBtn);
            pathsGroup.Add(settingsPathRow);

            creationGroup.Add(pathsGroup);

            // Parent Root Dropdown
            var rootChoices = GetSceneRootNames();
            _parentRootDropdown = new DropdownField("Parent Root", rootChoices, 0);
            if (rootChoices.Contains(_wizardParentRootName))
                _parentRootDropdown.value = _wizardParentRootName;
            _parentRootDropdown.RegisterValueChangedCallback(evt => _wizardParentRootName = evt.newValue);
            creationGroup.Add(_parentRootDropdown);

            // Assemblies Multi-select Foldout
            var foldout = new Foldout { text = $"Assembly Scopes ({_wizardSelectedAssemblies.Count} selected)", value = false };
            foldout.style.marginTop = 6;
            
            var scrollHeight = Mathf.Min(_wizardAvailableAssemblies.Count * 20 + 5, 120);
            var assembliesScroll = new ScrollView { style = { height = scrollHeight, borderLeftWidth = 1, borderLeftColor = new StyleColor(NexusEditorStyles.BorderColor), paddingLeft = 10 } };
            
            foreach (var assemblyName in _wizardAvailableAssemblies)
            {
                var toggle = new Toggle(assemblyName) { value = _wizardSelectedAssemblies.Contains(assemblyName) };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                        _wizardSelectedAssemblies.Add(assemblyName);
                    else
                        _wizardSelectedAssemblies.Remove(assemblyName);
                    foldout.text = $"Assembly Scopes ({_wizardSelectedAssemblies.Count} selected)";
                });
                assembliesScroll.Add(toggle);
            }
            foldout.Add(assembliesScroll);
            creationGroup.Add(foldout);

            // Lifecycle toggles
            var toggleLifecycle = new Toggle("Create Lifecycle Template") { value = _wizardGenerateLifecycleScript };
            var toggleBoilerplate = new Toggle("Create Architecture Boilerplate") { value = _wizardGenerateSampleArchitecture };
            
            toggleLifecycle.RegisterValueChangedCallback(evt =>
            {
                _wizardGenerateLifecycleScript = evt.newValue;
                toggleBoilerplate.SetEnabled(_wizardGenerateLifecycleScript);
                if (!_wizardGenerateLifecycleScript) toggleBoilerplate.value = false;
            });
            toggleBoilerplate.RegisterValueChangedCallback(evt => _wizardGenerateSampleArchitecture = evt.newValue);
            
            creationGroup.Add(toggleLifecycle);
            creationGroup.Add(toggleBoilerplate);

            // Validation & Build Action
            _validationLabel = new Label { style = { color = new StyleColor(NexusEditorStyles.AccentOrange), fontSize = 10, marginTop = 8, whiteSpace = WhiteSpace.Normal } };
            creationGroup.Add(_validationLabel);

            _createRootButton = NexusEditorStyles.CreateButton("Create Root & ContextData", RunCreateRoot, NexusEditorStyles.BtnBlue);
            creationGroup.Add(_createRootButton);

            ValidateCreateRootForm();
        }

        private void BuildViewMediatorGenTab()
        {
            var genGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, "GENERATE VIEW & MEDIATOR");

            var viewNameField = new TextField("View Name") { value = _wizardViewName };
            viewNameField.RegisterValueChangedCallback(evt => { _wizardViewName = evt.newValue; ValidateViewGenForm(viewNameField); });
            genGroup.Add(viewNameField);

            var rootChoices = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            if (rootChoices.Count == 0)
            {
                var errorLabel = new Label("No active Roots found in scene. Create a Root first.") { style = { color = Color.red, fontSize = 10, marginTop = 5 } };
                genGroup.Add(errorLabel);
                return;
            }

            if (string.IsNullOrEmpty(_wizardViewTargetRootName) || !rootChoices.Contains(_wizardViewTargetRootName))
            {
                _wizardViewTargetRootName = rootChoices[0];
            }

            _viewTargetRootDropdown = new DropdownField("Target Root Context", rootChoices, rootChoices.IndexOf(_wizardViewTargetRootName));
            _viewTargetRootDropdown.RegisterValueChangedCallback(evt => _wizardViewTargetRootName = evt.newValue);
            genGroup.Add(_viewTargetRootDropdown);

            var toggleCreateGo = new Toggle("Create GameObject in Scene") { value = _wizardCreateViewGo };
            toggleCreateGo.RegisterValueChangedCallback(evt => _wizardCreateViewGo = evt.newValue);
            genGroup.Add(toggleCreateGo);

            var genBtn = NexusEditorStyles.CreateButton("Generate View & Mediator Files", RunGenerateViewAndMediator, NexusEditorStyles.BtnBlue);
            genGroup.Add(genBtn);
        }

        private void BuildCleanDeletionTab()
        {
            var deleteGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, "CLEAN DELETION TOOL");

            var rootChoices = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            if (rootChoices.Count == 0)
            {
                var errorLabel = new Label("No active Roots found in scene.") { style = { color = Color.gray, fontSize = 10, marginTop = 5 } };
                deleteGroup.Add(errorLabel);
                return;
            }

            if (string.IsNullOrEmpty(_wizardRootToDeleteName) || !rootChoices.Contains(_wizardRootToDeleteName))
            {
                _wizardRootToDeleteName = rootChoices[0];
            }

            _deleteRootDropdown = new DropdownField("Root Context to Delete", rootChoices, rootChoices.IndexOf(_wizardRootToDeleteName));
            _deleteRootDropdown.RegisterValueChangedCallback(evt => _wizardRootToDeleteName = evt.newValue);
            deleteGroup.Add(_deleteRootDropdown);

            var warnText = "WARNING: This will permanently delete:\n" +
                           "- The Root GameObject from the active scene.\n" +
                           "- The associated ContextData ScriptableObject.\n" +
                           "- The generated script directory under Assets/Scripts/Nexus/<ContextName>/\n\n" +
                           "Make sure you have backed up your custom script changes before committing!";
            var warningBox = NexusEditorStyles.CreateWarningBox(warnText);
            deleteGroup.Add(warningBox);

            var deleteBtn = NexusEditorStyles.CreateButton("DELETE ROOT & ALL RELATED ASSETS", RunDeleteRootContext, NexusEditorStyles.AccentRed);
            deleteGroup.Add(deleteBtn);
        }

        private void BrowseFolder(Action<string> onFolderSelected)
        {
            string folder = EditorUtility.OpenFolderPanel("Select Folder", "Assets", "");
            if (!string.IsNullOrEmpty(folder))
            {
                // Convert absolute path to relative assets path
                if (folder.StartsWith(Application.dataPath))
                {
                    string relativePath = "Assets" + folder.Substring(Application.dataPath.Length);
                    onFolderSelected(relativePath);
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Selected folder must be inside the Project Assets folder.", "OK");
                }
            }
        }

        private void UpdateDropdownChoices()
        {
            if (_parentRootDropdown != null)
            {
                var choices = GetSceneRootNames();
                _parentRootDropdown.choices = choices;
                if (!choices.Contains(_wizardParentRootName))
                {
                    _wizardParentRootName = "None (Root Context)";
                    _parentRootDropdown.value = _wizardParentRootName;
                }
            }

            var rootNames = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            
            if (_viewTargetRootDropdown != null)
            {
                _viewTargetRootDropdown.choices = rootNames;
                if (rootNames.Count > 0 && !rootNames.Contains(_wizardViewTargetRootName))
                {
                    _wizardViewTargetRootName = rootNames[0];
                    _viewTargetRootDropdown.value = _wizardViewTargetRootName;
                }
            }

            if (_deleteRootDropdown != null)
            {
                _deleteRootDropdown.choices = rootNames;
                if (rootNames.Count > 0 && !rootNames.Contains(_wizardRootToDeleteName))
                {
                    _wizardRootToDeleteName = rootNames[0];
                    _deleteRootDropdown.value = _wizardRootToDeleteName;
                }
            }
        }

        private void ValidateCreateRootForm()
        {
            if (_validationLabel == null) return;

            bool isValid = true;
            string errorText = "";

            if (string.IsNullOrWhiteSpace(_wizardContextName))
            {
                isValid = false;
                errorText = "Context Name cannot be empty.";
            }
            else if (_wizardContextName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                isValid = false;
                errorText = "Context Name contains invalid path characters.";
            }
            else if (string.IsNullOrWhiteSpace(_wizardScopeTag))
            {
                isValid = false;
                errorText = "Scope Tag cannot be empty.";
            }
            else
            {
                string path = Path.Combine(_wizardSettingsPath, $"{_wizardContextName}ContextData.asset");
                if (File.Exists(path))
                {
                    isValid = false;
                    errorText = $"A ContextData asset already exists at {path}. Use a different Context Name.";
                }
            }

            _validationLabel.text = errorText;
            _validationLabel.style.display = isValid ? DisplayStyle.None : DisplayStyle.Flex;
            _createRootButton.SetEnabled(isValid);
        }

        private void ValidateViewGenForm(TextField field)
        {
            var btn = _subTabContent.Q<Button>();
            if (btn != null)
            {
                btn.SetEnabled(!string.IsNullOrWhiteSpace(_wizardViewName));
            }
        }

        private List<string> GetSceneRootNames()
        {
            var names = new List<string> { "None (Root Context)" };
            foreach (var r in _cachedSceneRoots)
            {
                names.Add(r.gameObject.name);
            }
            return names;
        }

        private void PopulateAvailableAssemblies()
        {
            _wizardAvailableAssemblies.Clear();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

        private void RefreshSceneRoots()
        {
            _cachedSceneRoots = GameObject.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
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
            manifest.DefaultContextNames = new[] { "Global", "Gameplay", "UI" };
            manifest.GenerateSampleSignals = true;
            manifest.GenerateSampleCommands = true;
            manifest.EnableInspector = true;

            EnsureFolderExists(_wizardSettingsPath);

            string path = Path.Combine(_wizardSettingsPath, "NexusBootstrapManifest.asset");
            AssetDatabase.CreateAsset(manifest, path);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Nexus] Created default Bootstrap Manifest at {path}");
            AssetDatabase.Refresh();
            RenderSubTab();
        }

        private void GenerateSkeleton(NexusBootstrapManifest manifest)
        {
            foreach (var name in manifest.DefaultContextNames)
            {
                CreateRootContext(name, name);
            }

            if (manifest.GenerateSampleSignals || manifest.GenerateSampleCommands)
            {
                string samplesDir = "Assets/Samples/Nexus";
                EnsureFolderExists(samplesDir);

                if (manifest.GenerateSampleSignals)
                {
                    string signalPath = Path.Combine(samplesDir, "SampleSignals.cs");
                    File.WriteAllText(signalPath, NexusTemplateProvider.GetSampleSignalCode());
                }

                if (manifest.GenerateSampleCommands)
                {
                    string commandPath = Path.Combine(samplesDir, "SampleCommands.cs");
                    File.WriteAllText(commandPath, NexusTemplateProvider.GetSampleCommandCode());
                }

                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Nexus Setup", "Skeleton generated successfully!", "OK");
            OnHierarchyChanged();
        }

        private void RunCreateRoot()
        {
            CreateRootContext(_wizardContextName, _wizardScopeTag);
        }

        private void CreateRootContext(string contextName, string scopeTag)
        {
            var go = new GameObject($"{contextName}Root");
            var root = go.AddComponent<Root>();

            Root parentRoot = null;
            if (_wizardParentRootName != "None (Root Context)")
            {
                parentRoot = _cachedSceneRoots.FirstOrDefault(r => r.gameObject.name == _wizardParentRootName);
                if (parentRoot != null)
                {
                    go.transform.SetParent(parentRoot.transform);
                }
            }

            var contextData = ScriptableObject.CreateInstance<ContextData>();
            contextData.ScopeTag = scopeTag;
            contextData.AssemblyScopes = _wizardSelectedAssemblies.ToArray();

            EnsureFolderExists(_wizardSettingsPath);

            string path = Path.Combine(_wizardSettingsPath, $"{contextName}ContextData.asset");
            AssetDatabase.CreateAsset(contextData, path);
            AssetDatabase.SaveAssets();

            var serializedRoot = new SerializedObject(root);
            var contextDataProp = serializedRoot.FindProperty("contextData");
            if (contextDataProp != null)
                contextDataProp.objectReferenceValue = contextData;

            if (parentRoot != null)
            {
                var parentProp = serializedRoot.FindProperty("parentRoot");
                if (parentProp != null) parentProp.objectReferenceValue = parentRoot;
            }
            serializedRoot.ApplyModifiedProperties();

            if (_wizardGenerateLifecycleScript)
            {
                EnsureFolderExists(_wizardScriptsPath);

                string scriptPath;
                if (_wizardGenerateSampleArchitecture)
                {
                    string contextDir = Path.Combine(_wizardScriptsPath, contextName);
                    string signalsDir = Path.Combine(contextDir, "Signals");
                    string modelsDir = Path.Combine(contextDir, "Models");
                    string commandsDir = Path.Combine(contextDir, "Commands");
                    string viewsDir = Path.Combine(contextDir, "Views");

                    EnsureFolderExists(contextDir);
                    EnsureFolderExists(signalsDir);
                    EnsureFolderExists(modelsDir);
                    EnsureFolderExists(commandsDir);
                    EnsureFolderExists(viewsDir);

                    File.WriteAllText(Path.Combine(signalsDir, $"{contextName}Signals.cs"), NexusTemplateProvider.GetSignalsBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"I{contextName}Model.cs"), NexusTemplateProvider.GetModelInterfaceBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(modelsDir, $"{contextName}Model.cs"), NexusTemplateProvider.GetModelImplementationBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(commandsDir, $"{contextName}Command.cs"), NexusTemplateProvider.GetCommandBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}View.cs"), NexusTemplateProvider.GetViewBoilerplate(contextName));
                    File.WriteAllText(Path.Combine(viewsDir, $"{contextName}Mediator.cs"), NexusTemplateProvider.GetMediatorBoilerplate(contextName));

                    scriptPath = Path.Combine(contextDir, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, NexusTemplateProvider.GetLifecycleBoilerplateWithBindings(contextName));
                }
                else
                {
                    scriptPath = Path.Combine(_wizardScriptsPath, $"{contextName}Lifecycle.cs");
                    File.WriteAllText(scriptPath, NexusTemplateProvider.GetLifecycleTemplateCode(contextName));
                }
                Debug.Log($"[Nexus] Generated lifecycle template at {scriptPath}");
            }

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Nexus Root");

            AssetDatabase.Refresh();
            OnHierarchyChanged();

            ShowPostCreationGuide(go.name, contextName, scopeTag);
        }

        private void ShowPostCreationGuide(string goName, string contextName, string scopeTag)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                $"Root Created: {goName}",
                $"Successfully created {goName} (ScopeTag: {scopeTag}).\n\n" +
                "--- NEXT STEPS ---\n\n" +
                "1. Open Live Tracer to monitor signal flow in real-time.\n" +
                "2. Fill in Lifecycle and Command classes for your business logic.\n" +
                "3. Enter Play Mode to observe signal chains live.",
                "Open Live Tracer",
                "Open Signal Explorer",
                "OK"
            );

            if (choice == 0)
                Window.SwitchToPlugin("Tracer");
            else if (choice == 1)
                Window.SwitchToPlugin("Explorer");
        }

        private void RunGenerateViewAndMediator()
        {
            var targetRoot = _cachedSceneRoots.FirstOrDefault(r => r.gameObject.name == _wizardViewTargetRootName);
            if (targetRoot == null) return;

            string contextName = targetRoot.ContextData != null ? targetRoot.ContextData.name.Replace("ContextData", "") : targetRoot.gameObject.name.Replace("Root", "");
            string viewsDir = Path.Combine(_wizardScriptsPath, contextName, "Views");

            try
            {
                EnsureFolderExists(viewsDir);

                string viewPath = Path.Combine(viewsDir, $"{_wizardViewName}View.cs");
                string mediatorPath = Path.Combine(viewsDir, $"{_wizardViewName}Mediator.cs");

                if (File.Exists(viewPath) || File.Exists(mediatorPath))
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Files?", $"Files for {_wizardViewName}View already exist. Do you want to overwrite them?", "Yes", "No"))
                        return;
                }

                File.WriteAllText(viewPath, NexusTemplateProvider.GetGenericViewBoilerplate(_wizardViewName, contextName));
                File.WriteAllText(mediatorPath, NexusTemplateProvider.GetGenericMediatorBoilerplate(_wizardViewName, contextName));

                if (_wizardCreateViewGo)
                {
                    EditorPrefs.SetString("com.nexus.core.PendingViewName", _wizardViewName);
                    EditorPrefs.SetString("com.nexus.core.PendingViewRootName", targetRoot.gameObject.name);
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Generated successfully", $"Successfully generated {_wizardViewName}View and {_wizardViewName}Mediator under {viewsDir}.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] View/Mediator generation failed: {ex.Message}");
            }
        }

        private void RunDeleteRootContext()
        {
            var targetRoot = _cachedSceneRoots.FirstOrDefault(r => r.gameObject.name == _wizardRootToDeleteName);
            if (targetRoot == null) return;

            string contextName = targetRoot.ContextData != null ? targetRoot.ContextData.name.Replace("ContextData", "") : targetRoot.gameObject.name.Replace("Root", "");

            if (!EditorUtility.DisplayDialog("Confirm Clean Deletion", 
                $"Are you absolutely sure you want to delete context '{contextName}' and all its assets/GameObjects? This action cannot be fully undone.", 
                "Yes, Delete", "Cancel"))
            {
                return;
            }

            try
            {
                var go = targetRoot.gameObject;

                if (targetRoot.ContextData != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(targetRoot.ContextData);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                }

                string scriptsDir = Path.Combine(_wizardScriptsPath, contextName);
                if (AssetDatabase.IsValidFolder(scriptsDir))
                    AssetDatabase.DeleteAsset(scriptsDir);
                else
                {
                    string flatScriptPath = Path.Combine(_wizardScriptsPath, $"{contextName}Lifecycle.cs");
                    if (File.Exists(flatScriptPath)) AssetDatabase.DeleteAsset(flatScriptPath);
                }

                Undo.DestroyObjectImmediate(go);
                AssetDatabase.Refresh();
                OnHierarchyChanged();
                EditorUtility.DisplayDialog("Root Deleted", $"Successfully deleted context '{contextName}' and its related assets.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Failed to cleanly delete root: {ex.Message}");
            }
        }

        private void EnsureFolderExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }
}
