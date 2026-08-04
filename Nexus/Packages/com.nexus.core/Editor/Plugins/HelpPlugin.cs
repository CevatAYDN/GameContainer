using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// In-editor Nexus documentation browser.
    /// Provides quick-start guides, API reference, and best practices.
    /// </summary>
    public class HelpPlugin : NexusEditorPlugin
    {
        public override string Id => "Help";
        public override string DisplayName => NexusLang.Get("action_help_title");
        public override int Order => 12;

        private VisualElement _view;
        private ScrollView _scrollView;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("help_title"));
            _view.Add(toolbar);

            _scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 20, paddingRight = 20, paddingTop = 15, paddingBottom = 15 } };
            _view.Add(_scrollView);

            RenderQuickStart();
            RenderAPISummary();
            RenderVersionInfo();
            RenderSamples();

            return _view;
        }

        public override void OnDisable()
        {
            _scrollView = null;
            _view = null;
            base.OnDisable();
        }

        private void RenderQuickStart()
        {
            AddSection(NexusLang.Get("help_quickstart"), NexusEditorStyles.AccentBlue);

            AddStep(NexusLang.Get("help_step1_title"),  NexusLang.Get("help_step1_desc"));
            AddStep(NexusLang.Get("help_step2_title"),  NexusLang.Get("help_step2_desc"));
            AddStep(NexusLang.Get("help_step3_title"),  NexusLang.Get("help_step3_desc"));
            AddStep(NexusLang.Get("help_step4_title"),  NexusLang.Get("help_step4_desc"));
            AddStep(NexusLang.Get("help_step5_title"),  NexusLang.Get("help_step5_desc"));
            AddStep(NexusLang.Get("help_step6_title"),  NexusLang.Get("help_step6_desc"));
        }

        private void RenderAPISummary()
        {
            AddSection(NexusLang.Get("help_coreapi"), NexusEditorStyles.AccentPurple);
            AddCard(NexusLang.Get("help_card_signalbus"),        NexusLang.Get("help_card_signalbus_content"));
            AddCard(NexusLang.Get("help_card_contextbuilder"),   NexusLang.Get("help_card_contextbuilder_content"));
            AddCard(NexusLang.Get("help_card_execmodes"),        NexusLang.Get("help_card_execmodes_content"));
            AddCard(NexusLang.Get("help_card_attributes"),       NexusLang.Get("help_card_attributes_content"));
            AddCard(NexusLang.Get("help_card_recovery"),         NexusLang.Get("help_card_recovery_content"));
        }

        private void RenderVersionInfo()
        {
            AddSection(NexusLang.Get("help_version_section"), NexusEditorStyles.AccentGreen);

            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    marginTop = 5,
                    marginBottom = 10,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 8,
                    paddingBottom = 8,
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                }
            };

            card.Add(new Label(NexusLang.Get("help_version"))
            {
                style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue) }
            });

            card.Add(new Label(NexusLang.Get("help_platform"))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 4 }
            });

            card.Add(new Label(NexusLang.Get("help_whats_new"))
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 4, whiteSpace = WhiteSpace.Normal }
            });

            _scrollView.Add(card);
        }

        private void RenderSamples()
        {
            AddSection(NexusLang.Get("help_samples"), NexusEditorStyles.AccentOrange);

            var importBtn = NexusEditorStyles.CreateButton(NexusLang.Get("help_import_sample"), () =>
            {
                EditorApplication.ExecuteMenuItem("Window/Package Manager");
                Debug.Log("[Nexus] Open Package Manager → Nexus Observable Architecture → Samples to import the Counter example.");
            }, NexusEditorStyles.BtnBlue);
            _scrollView.Add(importBtn);

            var hint = NexusEditorStyles.CreateHint(NexusLang.Get("help_samples_hint"));
            hint.style.marginTop = 4;
            _scrollView.Add(hint);
        }

        // ─── Helpers ───────────────────────────────────────────
        private void AddSection(string title, Color accent)
        {
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 12, marginBottom = 6 } };
            header.Add(NexusEditorStyles.CreateStatusDot(accent, 8));
            header.Add(new Label(title)
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 6 }
            });
            _scrollView.Add(header);
        }

        private void AddStep(string title, string description)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 3, marginLeft = 15 } };
            row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentBlue, 5));
            var text = new Label($"<b>{title}</b>\n{description}")
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 5, whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
            };
            row.Add(text);
            _scrollView.Add(row);
        }

        private void AddCard(string title, string content)
        {
            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    marginTop = 4,
                    marginBottom = 4,
                    marginLeft = 15,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 6,
                    paddingBottom = 6,
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                }
            };

            var titleLabel = new Label(title)
            {
                style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentYellow), marginBottom = 3 }
            };
            card.Add(titleLabel);

            var contentLabel = new Label(content)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), whiteSpace = WhiteSpace.Normal }
            };
            card.Add(contentLabel);

            _scrollView.Add(card);
        }
    }
}
