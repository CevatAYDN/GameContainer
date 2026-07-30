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
        public override string DisplayName => NexusLang.Get("action_wizard_title");
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

        // Must match SubTab enum value order: CreateRoot, ServiceGen, ViewMediatorGen, CleanDeletion, SignalCommandGen
        private readonly IWizardTab[] _tabs = new IWizardTab[]
        {
            new CreateRootTab(),       // [0] CreateRoot
            new ServiceGenTab(),       // [1] ServiceGen
            new ViewMediatorGenTab(),  // [2] ViewMediatorGen
            new CleanDeletionTab(),    // [3] CleanDeletion
            new SignalCommandGenTab()  // [4] SignalCommandGen
        };

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
            base.OnDisable();
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

            int idx = (int)_selectedSubTab;
            if (idx >= 0 && idx < _tabs.Length)
                _tabs[idx].BuildUI(_subTabContent);
        }

        // ─── (Dead Build*Tab methods removed — migrated to WizardTabs.cs) ───

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

        // ─── (More dead action methods removed — migrated to WizardTabs.cs + NexusSetupWizardHelper) ───
    }
}
