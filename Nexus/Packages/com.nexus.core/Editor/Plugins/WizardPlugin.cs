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
            ServiceGen = 1,
            ViewMediatorGen = 2,
            CleanDeletion = 3,
            SignalCommandGen = 4
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

        // Factory Modules
        private bool _wizardModIAP = false;
        private bool _wizardModAds = false;
        private bool _wizardModAnalytics = false;
        private bool _wizardModInventory = false;

        // Inputs for Service Gen
        private string _wizardServiceName = "PlayerDataService";

        // Inputs for View/Mediator Gen
        private string _wizardViewName = "GameplayHUD";
        private string _wizardViewTargetRootName = "";
        private bool _wizardCreateViewGo = true;

        // Inputs for Signal/Command Gen
        private string _wizardSignalName = "PlayerScoreChanged";
        private string _wizardCommandName = "UpdateScoreCommand";
        private string _wizardSignalTargetRootName = "";

        // Inputs for Clean Deletion
        private string _wizardRootToDeleteName = "";

        // UI Element References
        private VisualElement _contentRoot;
        private VisualElement _subTabContent;
        private Label _validationLabel;
        private Button _createRootButton;
        
        private DropdownField _parentRootDropdown;
        private DropdownField _viewTargetRootDropdown;
        private DropdownField _signalTargetRootDropdown;
        private DropdownField _deleteRootDropdown;

        private List<string> _wizardAvailableAssemblies = new();
        private Root[] _cachedSceneRoots = Array.Empty<Root>();

        private SubTab _selectedSubTab = SubTab.CreateRoot;

        public override VisualElement CreateView()
        {
            _contentRoot = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("wizard_title"));
            _contentRoot.Add(toolbar);

            // Tab navigation buttons
            var tabHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) } };
            
            var btnCreateRoot = CreateSubTabButton(NexusLang.Get("wizard_subtab_create_root"), SubTab.CreateRoot);
            var btnServiceGen = CreateSubTabButton(NexusLang.Get("wizard_subtab_service_gen"), SubTab.ServiceGen);
            var btnViewGen = CreateSubTabButton(NexusLang.Get("wizard_subtab_view_gen"), SubTab.ViewMediatorGen);
            var btnSignalCmdGen = CreateSubTabButton(NexusLang.Get("wizard_subtab_signal_gen"), SubTab.SignalCommandGen);
            var btnDelete = CreateSubTabButton(NexusLang.Get("wizard_subtab_clean_deletion"), SubTab.CleanDeletion);

            tabHeader.Add(btnCreateRoot);
            tabHeader.Add(btnServiceGen);
            tabHeader.Add(btnViewGen);
            tabHeader.Add(btnSignalCmdGen);
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
                case SubTab.ServiceGen:
                    BuildServiceGenTab();
                    break;
                case SubTab.ViewMediatorGen:
                    BuildViewMediatorGenTab();
                    break;
                case SubTab.SignalCommandGen:
                    BuildSignalCommandGenTab();
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
            var manifestGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_manifest"));
            if (manifest == null)
            {
                var hint = NexusEditorStyles.CreateHint(NexusLang.Get("wizard_hint_no_manifest"));
                manifestGroup.Add(hint);

                var createBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_create_manifest"), CreateDefaultManifest, NexusEditorStyles.BtnBlue);
                manifestGroup.Add(createBtn);
            }
            else
            {
                var details = new VisualElement { style = { paddingLeft = 10, marginTop = 4 } };
                details.Add(new Label(string.Format(NexusLang.Get("wizard_label_active_manifest"), manifest.name)) { style = { color = Color.white, fontSize = 10 } });
                details.Add(new Label(string.Format(NexusLang.Get("wizard_label_default_contexts"), string.Join(", ", manifest.DefaultContextNames))) { style = { color = NexusEditorStyles.TextSecondary, fontSize = 9 } });
                manifestGroup.Add(details);

                var genBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_gen_skeleton"), () => GenerateSkeleton(manifest), NexusEditorStyles.BtnBlue);
                manifestGroup.Add(genBtn);
            }

            // Section 2: Custom Root Creation
            var creationGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_create_root"));

            // Input Fields
            var contextNameField = new TextField(NexusLang.Get("wizard_field_context_name")) { value = _wizardContextName };
            contextNameField.RegisterValueChangedCallback(evt => { _wizardContextName = evt.newValue; ValidateCreateRootForm(); });
            creationGroup.Add(contextNameField);

            var scopeTagField = new TextField(NexusLang.Get("wizard_field_scope_tag")) { value = _wizardScopeTag };
            scopeTagField.RegisterValueChangedCallback(evt => { _wizardScopeTag = evt.newValue; ValidateCreateRootForm(); });
            creationGroup.Add(scopeTagField);

            // Path Configuration
            var pathsGroup = new VisualElement { style = { marginTop = 5, borderTopWidth = 1, borderTopColor = new StyleColor(NexusEditorStyles.BorderColor), paddingTop = 5 } };
            pathsGroup.Add(new Label(NexusLang.Get("wizard_paths_config")) { style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = NexusEditorStyles.TextSecondary, marginBottom = 4 } });

            var scriptsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var scriptsPathField = new TextField(NexusLang.Get("wizard_field_scripts_folder")) { value = _wizardScriptsPath, style = { flexGrow = 1 } };
            scriptsPathField.RegisterValueChangedCallback(evt => { _wizardScriptsPath = evt.newValue; ValidateCreateRootForm(); });
            scriptsPathRow.Add(scriptsPathField);
            var browseScriptsBtn = new Button(() => BrowseFolder(path => { scriptsPathField.value = path; })) { text = NexusLang.Get("wizard_browse") };
            scriptsPathRow.Add(browseScriptsBtn);
            pathsGroup.Add(scriptsPathRow);

            var settingsPathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
            var settingsPathField = new TextField(NexusLang.Get("wizard_field_settings_folder")) { value = _wizardSettingsPath, style = { flexGrow = 1 } };
            settingsPathField.RegisterValueChangedCallback(evt => { _wizardSettingsPath = evt.newValue; ValidateCreateRootForm(); });
            settingsPathRow.Add(settingsPathField);
            var browseSettingsBtn = new Button(() => BrowseFolder(path => { settingsPathField.value = path; })) { text = NexusLang.Get("wizard_browse") };
            settingsPathRow.Add(browseSettingsBtn);
            pathsGroup.Add(settingsPathRow);

            creationGroup.Add(pathsGroup);

            // Parent Root Dropdown
            var rootChoices = GetSceneRootNames();
            _parentRootDropdown = new DropdownField(NexusLang.Get("wizard_field_parent_root"), rootChoices, 0);
            if (rootChoices.Contains(_wizardParentRootName))
                _parentRootDropdown.value = _wizardParentRootName;
            _parentRootDropdown.RegisterValueChangedCallback(evt => _wizardParentRootName = evt.newValue);
            creationGroup.Add(_parentRootDropdown);

            // Assemblies Multi-select Foldout
            var foldout = new Foldout { text = string.Format(NexusLang.Get("wizard_assembly_scopes"), _wizardSelectedAssemblies.Count), value = false };
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
                    foldout.text = string.Format(NexusLang.Get("wizard_assembly_scopes"), _wizardSelectedAssemblies.Count);
                });
                assembliesScroll.Add(toggle);
            }
            foldout.Add(assembliesScroll);
            creationGroup.Add(foldout);

            // Lifecycle toggles
            var toggleLifecycle = new Toggle(NexusLang.Get("wizard_toggle_lifecycle")) { value = _wizardGenerateLifecycleScript };
            var toggleBoilerplate = new Toggle(NexusLang.Get("wizard_toggle_boilerplate")) { value = _wizardGenerateSampleArchitecture };
            
            toggleLifecycle.RegisterValueChangedCallback(evt =>
            {
                _wizardGenerateLifecycleScript = evt.newValue;
                toggleBoilerplate.SetEnabled(_wizardGenerateLifecycleScript);
                if (!_wizardGenerateLifecycleScript) toggleBoilerplate.value = false;
            });
            toggleBoilerplate.RegisterValueChangedCallback(evt => _wizardGenerateSampleArchitecture = evt.newValue);
            
            creationGroup.Add(toggleLifecycle);
            creationGroup.Add(toggleBoilerplate);

            // Factory Modules
            var modulesFoldout = new Foldout { text = NexusLang.Get("wizard_foldout_modules"), value = true, style = { marginTop = 5 } };
            
            var toggleIAP = new Toggle(NexusLang.Get("wizard_toggle_iap")) { value = _wizardModIAP };
            toggleIAP.RegisterValueChangedCallback(evt => _wizardModIAP = evt.newValue);
            modulesFoldout.Add(toggleIAP);

            var toggleAds = new Toggle(NexusLang.Get("wizard_toggle_ads")) { value = _wizardModAds };
            toggleAds.RegisterValueChangedCallback(evt => _wizardModAds = evt.newValue);
            modulesFoldout.Add(toggleAds);

            var toggleAnalytics = new Toggle(NexusLang.Get("wizard_toggle_analytics")) { value = _wizardModAnalytics };
            toggleAnalytics.RegisterValueChangedCallback(evt => _wizardModAnalytics = evt.newValue);
            modulesFoldout.Add(toggleAnalytics);

            var toggleInventory = new Toggle(NexusLang.Get("wizard_toggle_inventory")) { value = _wizardModInventory };
            toggleInventory.RegisterValueChangedCallback(evt => _wizardModInventory = evt.newValue);
            modulesFoldout.Add(toggleInventory);

            creationGroup.Add(modulesFoldout);

            // Validation & Build Action
            _validationLabel = new Label { style = { color = new StyleColor(NexusEditorStyles.AccentOrange), fontSize = 10, marginTop = 8, whiteSpace = WhiteSpace.Normal } };
            creationGroup.Add(_validationLabel);

            _createRootButton = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_create_root"), RunCreateRoot, NexusEditorStyles.BtnBlue);
            creationGroup.Add(_createRootButton);

            ValidateCreateRootForm();
        }

        private void BuildViewMediatorGenTab()
        {
            var genGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_view_gen"));

            var viewNameField = new TextField(NexusLang.Get("wizard_field_view_name")) { value = _wizardViewName };
            viewNameField.RegisterValueChangedCallback(evt => { _wizardViewName = evt.newValue; ValidateViewGenForm(viewNameField); });
            genGroup.Add(viewNameField);

            var rootChoices = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            if (rootChoices.Count == 0)
            {
                var errorLabel = new Label(NexusLang.Get("wizard_no_roots")) { style = { color = Color.red, fontSize = 10, marginTop = 5 } };
                genGroup.Add(errorLabel);
                return;
            }

            if (string.IsNullOrEmpty(_wizardViewTargetRootName) || !rootChoices.Contains(_wizardViewTargetRootName))
            {
                _wizardViewTargetRootName = rootChoices[0];
            }

            _viewTargetRootDropdown = new DropdownField(NexusLang.Get("wizard_field_target_root"), rootChoices, rootChoices.IndexOf(_wizardViewTargetRootName));
            _viewTargetRootDropdown.RegisterValueChangedCallback(evt => _wizardViewTargetRootName = evt.newValue);
            genGroup.Add(_viewTargetRootDropdown);

            var toggleCreateGo = new Toggle(NexusLang.Get("wizard_toggle_create_go")) { value = _wizardCreateViewGo };
            toggleCreateGo.RegisterValueChangedCallback(evt => _wizardCreateViewGo = evt.newValue);
            genGroup.Add(toggleCreateGo);

            var genBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_gen_view"), RunGenerateViewAndMediator, NexusEditorStyles.BtnBlue);
            genGroup.Add(genBtn);
        }

        private void BuildSignalCommandGenTab()
        {
            var genGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_signal_gen"));

            var signalNameField = new TextField(NexusLang.Get("wizard_field_signal_name")) { value = _wizardSignalName };
            signalNameField.RegisterValueChangedCallback(evt => _wizardSignalName = evt.newValue);
            genGroup.Add(signalNameField);

            var commandNameField = new TextField(NexusLang.Get("wizard_field_command_name")) { value = _wizardCommandName };
            commandNameField.RegisterValueChangedCallback(evt => _wizardCommandName = evt.newValue);
            genGroup.Add(commandNameField);

            var rootChoices = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            if (rootChoices.Count == 0)
            {
                var errorLabel = new Label(NexusLang.Get("wizard_no_roots")) { style = { color = Color.red, fontSize = 10, marginTop = 5 } };
                genGroup.Add(errorLabel);
                return;
            }

            if (string.IsNullOrEmpty(_wizardSignalTargetRootName) || !rootChoices.Contains(_wizardSignalTargetRootName))
            {
                _wizardSignalTargetRootName = rootChoices[0];
            }

            _signalTargetRootDropdown = new DropdownField(NexusLang.Get("wizard_field_target_root"), rootChoices, rootChoices.IndexOf(_wizardSignalTargetRootName));
            _signalTargetRootDropdown.RegisterValueChangedCallback(evt => _wizardSignalTargetRootName = evt.newValue);
            genGroup.Add(_signalTargetRootDropdown);

            var genBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_gen_signal"), RunGenerateSignalAndCommand, NexusEditorStyles.BtnBlue);
            genGroup.Add(genBtn);
        }

        private void BuildServiceGenTab()
        {
            var genGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_service_gen"));
            genGroup.style.marginBottom = 8;

            var serviceNameField = new TextField(NexusLang.Get("wizard_field_service_name")) { value = _wizardServiceName };
            serviceNameField.RegisterValueChangedCallback(evt => { _wizardServiceName = evt.newValue; });
            genGroup.Add(serviceNameField);

            var serviceDescription = NexusEditorStyles.CreateHint(NexusLang.Get("wizard_hint_service_desc"));
            genGroup.Add(serviceDescription);

            var genBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_gen_service"), RunGenerateService, NexusEditorStyles.BtnBlue);
            genGroup.Add(genBtn);

            var advancedGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_binding_help"));
            var hint = NexusEditorStyles.CreateHint(NexusLang.Get("wizard_hint_binding_help"));
            advancedGroup.Add(hint);
        }

        private void BuildCleanDeletionTab()
        {
            var deleteGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_delete"));

            var rootChoices = _cachedSceneRoots.Select(r => r.gameObject.name).ToList();
            if (rootChoices.Count == 0)
            {
                var errorLabel = new Label(NexusLang.Get("wizard_no_roots_short")) { style = { color = Color.gray, fontSize = 10, marginTop = 5 } };
                deleteGroup.Add(errorLabel);
                return;
            }

            if (string.IsNullOrEmpty(_wizardRootToDeleteName) || !rootChoices.Contains(_wizardRootToDeleteName))
            {
                _wizardRootToDeleteName = rootChoices[0];
            }

            _deleteRootDropdown = new DropdownField(NexusLang.Get("wizard_field_root_delete"), rootChoices, rootChoices.IndexOf(_wizardRootToDeleteName));
            _deleteRootDropdown.RegisterValueChangedCallback(evt => _wizardRootToDeleteName = evt.newValue);
            deleteGroup.Add(_deleteRootDropdown);

            var warnText = NexusLang.Get("wizard_warning_delete");
            var warningBox = NexusEditorStyles.CreateWarningBox(warnText);
            deleteGroup.Add(warningBox);

            var deleteBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_delete_root"), RunDeleteRootContext, NexusEditorStyles.AccentRed);
            deleteGroup.Add(deleteBtn);

            var cleanerGroup = NexusEditorStyles.CreateActionGroup(_subTabContent, NexusLang.Get("wizard_section_dead_code"));
            var scanBtn = NexusEditorStyles.CreateButton(NexusLang.Get("wizard_scan_unused"), ScanForDeadSignals, NexusEditorStyles.BtnBlue);
            cleanerGroup.Add(scanBtn);
        }

        private void ScanForDeadSignals()
        {
            var signalTypes = new HashSet<Type>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                if (assembly.GetName().Name.StartsWith("System") || assembly.GetName().Name.StartsWith("Unity")) continue;
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsValueType && !type.IsPrimitive && !type.IsEnum && type.Name.EndsWith("Signal"))
                        {
                            signalTypes.Add(type);
                        }
                    }
                }
                catch { }
            }

            var usedSignals = new HashSet<Type>();
            var allScripts = AssetDatabase.FindAssets("t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            
            foreach (var path in allScripts)
            {
                if (path.Contains("Package") || path.Contains("Plugins")) continue;
                var content = File.ReadAllText(path);
                
                // Remove comment blocks before scanning to avoid false positives in code comments
                var cleanContent = System.Text.RegularExpressions.Regex.Replace(content, @"//.*|/\*[\s\S]*?\*/", "");
                
                foreach (var signal in signalTypes)
                {
                    // If signal name appears in code (other than its own definition)
                    if (cleanContent.Contains(signal.Name) && !path.EndsWith(signal.Name + ".cs"))
                    {
                        usedSignals.Add(signal);
                    }
                }
            }

            var deadSignals = signalTypes.Except(usedSignals).ToList();
            if (deadSignals.Count == 0)
            {
                EditorUtility.DisplayDialog("Scanner", NexusLang.Get("wizard_scanner_none"), "OK");
                return;
            }

            string report = NexusLang.Get("wizard_scanner_title") + "\n\n";
            foreach (var ds in deadSignals) report += $"- {ds.Name}\n";
            
            EditorUtility.DisplayDialog("Dead Signals Found", report, "OK");
        }

        private void BrowseFolder(Action<string> onFolderSelected)
        {
            string folder = EditorUtility.OpenFolderPanel(NexusLang.Get("wizard_browse_title"), "Assets", "");
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

            if (_signalTargetRootDropdown != null)
            {
                _signalTargetRootDropdown.choices = rootNames;
                if (rootNames.Count > 0 && !rootNames.Contains(_wizardSignalTargetRootName))
                {
                    _wizardSignalTargetRootName = rootNames[0];
                    _signalTargetRootDropdown.value = _wizardSignalTargetRootName;
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
            // Find the "Generate" button specifically, not the first button (which could be Browse).
            var genBtn = _subTabContent.Q<Button>(className: "nexus-btn");
            if (genBtn == null)
            {
                // Fallback: find by text content
                foreach (var child in _subTabContent.Children())
                {
                    if (child is Button btn && btn.text.StartsWith("Generate"))
                    {
                        genBtn = btn;
                        break;
                    }
                }
            }
            if (genBtn != null)
            {
                genBtn.SetEnabled(!string.IsNullOrWhiteSpace(_wizardViewName));
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
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
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
            try
            {
                CreateRootContext(_wizardContextName, _wizardScopeTag);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus Factory Error] {ex.Message}");
                EditorUtility.DisplayDialog("Nexus Factory Error", ex.Message, "OK");
            }
        }

        private void CreateRootContext(string contextName, string scopeTag)
        {
            if (string.IsNullOrWhiteSpace(contextName))
                throw new InvalidOperationException("Context Name cannot be empty.");

            string path = Path.Combine(_wizardSettingsPath, $"{contextName}ContextData.asset");
            if (File.Exists(path))
                throw new InvalidOperationException($"A ContextData asset already exists at {path}. Use a different Context Name or delete the existing one.");

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

            string assetPath = Path.Combine(_wizardSettingsPath, $"{contextName}ContextData.asset");
            AssetDatabase.CreateAsset(contextData, assetPath);
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

                    // Generate Factory Modules
                    if (_wizardModIAP) GenerateModuleSkeleton(contextDir, "IAP");
                    if (_wizardModAds) GenerateModuleSkeleton(contextDir, "Ads");
                    if (_wizardModAnalytics) GenerateModuleSkeleton(contextDir, "Analytics");
                    if (_wizardModInventory) GenerateModuleSkeleton(contextDir, "Inventory");

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

        private void GenerateModuleSkeleton(string parentDir, string moduleName)
        {
            string moduleDir = Path.Combine(parentDir, "Modules", moduleName);
            EnsureFolderExists(moduleDir);
            EnsureFolderExists(Path.Combine(moduleDir, "Signals"));
            EnsureFolderExists(Path.Combine(moduleDir, "Models"));
            EnsureFolderExists(Path.Combine(moduleDir, "Commands"));
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

        private void RunGenerateService()
        {
            if (string.IsNullOrWhiteSpace(_wizardServiceName))
            {
                EditorUtility.DisplayDialog("Error", "Service Name cannot be empty.", "OK");
                return;
            }

            var targetRoot = _cachedSceneRoots.FirstOrDefault();
            string contextName = targetRoot?.ContextData != null
                ? targetRoot.ContextData.name.Replace("ContextData", "")
                : "Gameplay";

            string servicesDir = Path.Combine(_wizardScriptsPath, contextName, "Services");

            try
            {
                EnsureFolderExists(servicesDir);

                string servicePath = Path.Combine(servicesDir, $"{_wizardServiceName}.cs");

                if (File.Exists(servicePath))
                {
                    if (!EditorUtility.DisplayDialog("Overwrite File?",
                            $"File for {_wizardServiceName} already exists. Overwrite?", "Yes", "No"))
                        return;
                }

                File.WriteAllText(servicePath,
                    NexusTemplateProvider.GetGenericServiceBoilerplate(_wizardServiceName, contextName));

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Generated successfully",
                    $"Successfully generated {_wizardServiceName} under {servicesDir}.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Service generation failed: {ex.Message}");
            }
        }

        private void RunGenerateSignalAndCommand()
        {
            var targetRoot = _cachedSceneRoots.FirstOrDefault(r => r.gameObject.name == _wizardSignalTargetRootName);
            if (targetRoot == null) return;

            string contextName = targetRoot.ContextData != null ? targetRoot.ContextData.name.Replace("ContextData", "") : targetRoot.gameObject.name.Replace("Root", "");
            string signalsDir = Path.Combine(_wizardScriptsPath, contextName, "Signals");
            string commandsDir = Path.Combine(_wizardScriptsPath, contextName, "Commands");

            try
            {
                EnsureFolderExists(signalsDir);
                EnsureFolderExists(commandsDir);

                string signalPath = Path.Combine(signalsDir, $"{_wizardSignalName}.cs");
                string commandPath = Path.Combine(commandsDir, $"{_wizardCommandName}.cs");

                if (File.Exists(signalPath) || File.Exists(commandPath))
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Files?", $"Files for {_wizardSignalName} or {_wizardCommandName} already exist. Do you want to overwrite them?", "Yes", "No"))
                        return;
                }

                File.WriteAllText(signalPath, NexusTemplateProvider.GetGenericSignalBoilerplate(_wizardSignalName, contextName));
                File.WriteAllText(commandPath, NexusTemplateProvider.GetGenericCommandBoilerplate(_wizardCommandName, _wizardSignalName, contextName));

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Generated successfully", $"Successfully generated {_wizardSignalName} under {signalsDir} and {_wizardCommandName} under {commandsDir}.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Signal/Command generation failed: {ex.Message}");
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
