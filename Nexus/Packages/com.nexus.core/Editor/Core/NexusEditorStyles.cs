using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    internal static class NexusEditorStyles
    {
        internal static readonly Color Background = new(0.12f, 0.12f, 0.14f);
        internal static readonly Color CardBg = new(0.18f, 0.18f, 0.2f);
        internal static readonly Color CardBgAlt = new(0.16f, 0.16f, 0.18f);
        internal static readonly Color CardBgGreen = new(0.14f, 0.18f, 0.14f);
        internal static readonly Color CardBgYellow = new(0.2f, 0.18f, 0.14f);
        internal static readonly Color CardBgRed = new(0.2f, 0.14f, 0.14f);
        internal static readonly Color AccentBlue = new(0.3f, 0.8f, 1f);
        internal static readonly Color AccentGreen = new(0.4f, 1f, 0.4f);
        internal static readonly Color AccentYellow = new(1f, 0.85f, 0.3f);
        internal static readonly Color AccentOrange = new(1f, 0.7f, 0.2f);
        internal static readonly Color AccentPurple = new(0.8f, 0.6f, 0.9f);
        internal static readonly Color TextPrimary = new(0.85f, 0.85f, 0.85f);
        internal static readonly Color TextSecondary = new(0.6f, 0.6f, 0.6f);
        internal static readonly Color BorderColor = new(0.2f, 0.2f, 0.22f);
        internal static readonly Color SignalBlue = new(0.7f, 0.85f, 1f);
        internal static readonly Color BtnBlue = new(0.2f, 0.35f, 0.5f);
        internal static readonly Color BtnPurple = new(0.3f, 0.2f, 0.4f);
        internal static readonly Color BtnTeal = new(0.2f, 0.3f, 0.3f);

        internal static readonly Color TitleColor = AccentBlue;
        internal static readonly Color ToolbarBg = new(0.1f, 0.1f, 0.12f);

        internal const float CardRadius = 6f;
        internal const float BtnRadius = 4f;
        internal const float ToolbarPadding = 8f;
        internal const float CardPadding = 12f;

        internal static VisualElement CreateCard(Color bgColor)
        {
            return new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(bgColor),
                    paddingLeft = CardPadding,
                    paddingRight = CardPadding,
                    paddingTop = 10,
                    paddingBottom = 10,
                    borderTopLeftRadius = CardRadius,
                    borderTopRightRadius = CardRadius,
                    borderBottomLeftRadius = CardRadius,
                    borderBottomRightRadius = CardRadius,
                    marginBottom = 8
                }
            };
        }

        internal static Label CreateTitle(string text, Color color, int fontSize = 11)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = fontSize,
                    color = new StyleColor(color),
                    marginBottom = 6
                }
            };
        }

        internal static Label CreateBody(string text, int fontSize = 11)
        {
            return new Label(text)
            {
                style =
                {
                    color = new StyleColor(TextPrimary),
                    fontSize = fontSize,
                    whiteSpace = WhiteSpace.Normal
                }
            };
        }

        internal static Label CreateHint(string text)
        {
            return new Label(text)
            {
                style =
                {
                    color = new StyleColor(TextSecondary),
                    fontSize = 10,
                    whiteSpace = WhiteSpace.Normal
                }
            };
        }

        internal static Button CreateButton(string label, System.Action onClick, Color bgColor)
        {
            var btn = new Button(onClick) { text = label };
            btn.style.backgroundColor = new StyleColor(bgColor);
            btn.style.color = Color.white;
            btn.style.fontSize = 11;
            btn.style.paddingLeft = 10;
            btn.style.paddingRight = 10;
            btn.style.paddingTop = 5;
            btn.style.paddingBottom = 5;
            btn.style.borderTopLeftRadius = BtnRadius;
            btn.style.borderTopRightRadius = BtnRadius;
            btn.style.borderBottomLeftRadius = BtnRadius;
            btn.style.borderBottomRightRadius = BtnRadius;
            btn.style.marginTop = 4;
            btn.style.marginBottom = 4;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;
            return btn;
        }

        internal static VisualElement CreateInfoCard(VisualElement parent, string title, Color titleColor, Color bgColor, string description)
        {
            var card = CreateCard(bgColor);

            var titleLabel = CreateTitle(title, titleColor);
            card.Add(titleLabel);

            if (!string.IsNullOrEmpty(description))
            {
                var desc = CreateBody(description);
                card.Add(desc);
            }

            parent.Add(card);
            return card;
        }

        internal static VisualElement CreateActionGroup(VisualElement parent, string groupTitle)
        {
            var card = CreateInfoCard(parent, groupTitle, TextSecondary, CardBgAlt, "");
            return card;
        }

        internal static void AddActionButton(VisualElement parent, string label, System.Action onClick, Color bgColor)
        {
            parent.Add(CreateButton(label, onClick, bgColor));
        }

        internal static VisualElement CreateToolbar(string windowTitle)
        {
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.backgroundColor = new StyleColor(ToolbarBg);
            toolbar.style.paddingLeft = 10;
            toolbar.style.paddingRight = 10;
            toolbar.style.paddingTop = ToolbarPadding;
            toolbar.style.paddingBottom = ToolbarPadding;
            toolbar.style.borderBottomWidth = 1;
            toolbar.style.borderBottomColor = new StyleColor(BorderColor);
            toolbar.style.alignItems = Align.Center;

            var titleLabel = new Label(windowTitle);
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 12;
            titleLabel.style.color = new StyleColor(AccentBlue);
            titleLabel.style.marginRight = 20;
            toolbar.Add(titleLabel);

            return toolbar;
        }

        internal static void AddToolbarButton(VisualElement toolbar, string label, System.Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.28f));
            btn.style.borderTopLeftRadius = BtnRadius;
            btn.style.borderTopRightRadius = BtnRadius;
            btn.style.borderBottomLeftRadius = BtnRadius;
            btn.style.borderBottomRightRadius = BtnRadius;
            btn.style.color = Color.white;
            btn.style.paddingLeft = 10;
            btn.style.paddingRight = 10;
            btn.style.marginLeft = 5;
            toolbar.Add(btn);
        }

        internal static Label CreateStatusBar()
        {
            return new Label
            {
                style =
                {
                    backgroundColor = new StyleColor(ToolbarBg),
                    color = new StyleColor(TextSecondary),
                    fontSize = 10,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 4,
                    paddingBottom = 4,
                    borderTopWidth = 1,
                    borderTopColor = new StyleColor(BorderColor),
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
        }
    }
}
