using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>The Context Wizard: creates a context (ContextData and Root) and generates services, views and signals.</summary>
    public class WizardPlugin : NexusEditorPlugin
    {
        public override string Id => "Wizard";
        public override string DisplayName => NexusLang.Get("action_wizard_title");
        public override int Order => 1;
        public override string Category => "cat_setup";

        private readonly IWizardTab[] _tabs =
        {
            new ContextTab(),
            new ServiceGenTab(),
            new ViewMediatorGenTab(),
            new SignalCommandGenTab()
        };

        private int _selected;
        private VisualElement _tabHeader;
        private ScrollView _content;

        public override VisualElement CreateView()
        {
            var root = new VisualElement { style = { flexGrow = 1 } };
            root.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("wizard_title")));

            _tabHeader = new VisualElement { style = { flexDirection = FlexDirection.Row, backgroundColor = new StyleColor(NexusEditorStyles.ToolbarBg), borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) } };
            for (int i = 0; i < _tabs.Length; i++)
                _tabHeader.Add(CreateSubTabButton(i));
            root.Add(_tabHeader);

            _content = new ScrollView { style = { flexGrow = 1, paddingLeft = 15, paddingRight = 15, paddingTop = 15, paddingBottom = 15 } };
            root.Add(_content);
            RenderSubTab();
            return root;
        }

        public override void OnDisable()
        {
            _tabHeader = null;
            _content = null;
            base.OnDisable();
        }

        private Button CreateSubTabButton(int index)
        {
            var btn = new Button(() => { _selected = index; RenderSubTab(); }) { text = _tabs[index].Title };
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

        private void RenderSubTab()
        {
            if (_content == null) return;
            for (int i = 0; i < _tabHeader.childCount; i++)
            {
                bool active = i == _selected;
                _tabHeader[i].style.backgroundColor = new StyleColor(active ? NexusEditorStyles.HighlightBg : Color.clear);
                _tabHeader[i].style.color = new StyleColor(active ? NexusEditorStyles.AccentBlue : NexusEditorStyles.TextPrimary);
            }
            _content.Clear();
            _tabs[_selected].BuildUI(_content);
        }
    }
}
