using System;
using UnityEngine;
using UnityEngine.UIElements;

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

        // UI Element References
        private VisualElement _contentRoot;
        private VisualElement _subTabContent;



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

            RenderSubTab();


            return _contentRoot;
        }

        private Button CreateSubTabButton(string label, SubTab tab)
        {
            var btn = new Button(() =>
            {
                _selectedSubTab = tab;
                HighlightActiveSubTab();
                RenderSubTab();
            })
            { text = label };

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

    }
}
