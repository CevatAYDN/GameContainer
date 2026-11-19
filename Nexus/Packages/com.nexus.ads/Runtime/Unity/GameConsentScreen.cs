using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// The game's own look for the consent screen (issue #430). Every field is optional. A <see cref="Tree"/> must contain a
    /// <see cref="Button"/> named <c>nexus-consent-accept</c> and one named <c>nexus-consent-reject</c>; it may also name
    /// Labels <c>nexus-consent-title</c>, <c>-body</c>, <c>-partners-heading</c>, <c>-partners</c> and <c>-policy</c>, which receive the
    /// game's texts. A tree without the two buttons is refused with an error and the built-in screen is shown instead.
    /// </summary>
    public sealed class ConsentScreenStyle
    {
        public PanelSettings Panel;
        public VisualTreeAsset Tree;
        public StyleSheet Sheet;
        /// <summary>Sorting order of the built-in panel, above the game's own UI by default; a game's own <see cref="Panel"/> keeps its order.</summary>
        public float SortingOrder = 30000f;
    }

    /// <summary>The consent screen's element names, shared by the built-in screen and a game's own UXML.</summary>
    public static class ConsentScreenNames
    {
        public const string Root = "nexus-consent-root";
        public const string Title = "nexus-consent-title";
        public const string Body = "nexus-consent-body";
        public const string PartnersHeading = "nexus-consent-partners-heading";
        public const string Partners = "nexus-consent-partners";
        public const string Policy = "nexus-consent-policy";
        public const string Accept = "nexus-consent-accept";
        public const string Reject = "nexus-consent-reject";
    }

    /// <summary>
    /// The built-in consent screen: UI Toolkit, a full-screen scrim and one card. Accept and Reject are the same size, colour and
    /// weight; neither is focused or pre-selected; nothing but the two buttons closes the screen.
    /// </summary>
    internal static class ConsentScreenView
    {
        internal struct Built
        {
            public VisualElement Root;
            public Button Accept, Reject;
        }

        internal static Built Build(ConsentTexts texts, ConsentScreenStyle style, Action<bool> onChoice)
        {
            Built built = style != null && style.Tree != null ? FromAsset(texts, style.Tree) : default;
            if (built.Root == null) built = FromCode(texts);
            if (style != null && style.Sheet != null) built.Root.styleSheets.Add(style.Sheet);
            built.Accept.clicked += () => onChoice(true);
            built.Reject.clicked += () => onChoice(false);
            return built;
        }

        private static Built FromAsset(ConsentTexts texts, VisualTreeAsset asset)
        {
            var root = new VisualElement { name = ConsentScreenNames.Root };
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Position;
            asset.CloneTree(root);
            var accept = root.Q<Button>(ConsentScreenNames.Accept);
            var reject = root.Q<Button>(ConsentScreenNames.Reject);
            if (accept == null || reject == null)
            {
                Debug.LogError("[Nexus Ads] the game's consent screen has no Button named '" + ConsentScreenNames.Accept + "' and one named '"
                    + ConsentScreenNames.Reject + "': the built-in screen is shown instead");
                return default;
            }
            accept.text = texts.Accept;
            reject.text = texts.Reject;
            SetText(root, ConsentScreenNames.Title, texts.Title);
            SetText(root, ConsentScreenNames.Body, texts.Body);
            SetText(root, ConsentScreenNames.PartnersHeading, texts.PartnersHeading);
            SetText(root, ConsentScreenNames.Partners, Join(texts));
            BindPolicy(root.Q<Label>(ConsentScreenNames.Policy), texts);
            return new Built { Root = root, Accept = accept, Reject = reject };
        }

        private static Built FromCode(ConsentTexts texts)
        {
            var root = new VisualElement { name = ConsentScreenNames.Root, pickingMode = PickingMode.Position };
            root.style.flexGrow = 1;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;
            root.style.backgroundColor = new Color(0f, 0f, 0f, 0.7f);

            var card = new VisualElement();
            card.style.width = Length.Percent(88);
            card.style.maxWidth = 720;
            card.style.maxHeight = Length.Percent(90);
            card.style.paddingTop = card.style.paddingBottom = 28;
            card.style.paddingLeft = card.style.paddingRight = 24;
            card.style.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius = card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 16;
            root.Add(card);

            card.Add(Text(ConsentScreenNames.Title, texts.Title, 40, FontStyle.Bold));
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "nexus-consent-scroll" };
            scroll.style.flexShrink = 1;
            scroll.style.marginTop = 16;
            scroll.Add(Text(ConsentScreenNames.Body, texts.Body, 30, FontStyle.Normal));
            if (texts.Partners != null && texts.Partners.Count > 0)
            {
                var heading = Text(ConsentScreenNames.PartnersHeading, texts.PartnersHeading, 30, FontStyle.Bold);
                heading.style.marginTop = 16;
                scroll.Add(heading);
                scroll.Add(Text(ConsentScreenNames.Partners, Join(texts), 28, FontStyle.Normal));
            }
            var policy = Text(ConsentScreenNames.Policy, texts.PrivacyPolicyLabel, 30, FontStyle.Normal);
            policy.style.marginTop = 16;
            policy.style.unityTextAlign = TextAnchor.MiddleLeft;
            policy.style.color = new Color(0.55f, 0.75f, 1f, 1f);
            BindPolicy(policy, texts);
            scroll.Add(policy);
            card.Add(scroll);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 24;
            var accept = Choice(ConsentScreenNames.Accept, texts.Accept);
            var reject = Choice(ConsentScreenNames.Reject, texts.Reject);
            reject.style.marginLeft = 16; // both are flex-grow 1 with one style: equal width, colour and weight
            row.Add(accept);
            row.Add(reject);
            card.Add(row);
            return new Built { Root = root, Accept = accept, Reject = reject };
        }

        private static Label Text(string name, string text, int size, FontStyle weight)
        {
            var label = new Label(text ?? "") { name = name };
            label.style.fontSize = size;
            label.style.unityFontStyleAndWeight = weight;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = Color.white;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            return label;
        }

        private static Button Choice(string name, string text)
        {
            var button = new Button { name = name, text = text ?? "", focusable = false };
            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.minHeight = 96;
            button.style.fontSize = 32;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = Color.white;
            button.style.backgroundColor = new Color(0.27f, 0.32f, 0.42f, 1f);
            button.style.borderTopWidth = button.style.borderBottomWidth = button.style.borderLeftWidth = button.style.borderRightWidth = 0;
            button.style.borderTopLeftRadius = button.style.borderTopRightRadius = button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = 12;
            return button;
        }

        private static string Join(ConsentTexts texts) => texts.Partners == null ? "" : string.Join("\n", texts.Partners);

        private static void SetText(VisualElement root, string name, string text)
        {
            var label = root.Q<Label>(name);
            if (label != null) label.text = text ?? "";
        }

        private static void BindPolicy(Label label, ConsentTexts texts)
        {
            if (label == null) return;
            label.text = texts.PrivacyPolicyLabel ?? "";
            string url = texts.PrivacyPolicyUrl;
            bool openable = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
            label.style.display = openable && !string.IsNullOrEmpty(label.text) ? DisplayStyle.Flex : DisplayStyle.None;
            if (openable) label.RegisterCallback<ClickEvent>(_ => Application.OpenURL(url));
        }
    }

    /// <summary>
    /// <see cref="IConsentScreen"/> over UI Toolkit: a <see cref="UIDocument"/> on its own object, created when the screen opens and
    /// destroyed when the player answers. Created and used on the main thread.
    /// </summary>
    public sealed class UitkConsentScreen : IConsentScreen
    {
        internal const string ThemeResource = "NexusConsentTheme";

        private readonly ConsentScreenStyle _style;
        private GameObject _host;
        private PanelSettings _ownedPanel;

        public UitkConsentScreen(ConsentScreenStyle style = null) { _style = style; }

        public void Show(ConsentTexts texts, Action<bool> onChoice)
        {
            Hide();
            _host = new GameObject("NexusConsentScreen");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            var panel = _style != null ? _style.Panel : null;
            if (panel == null)
            {
                _ownedPanel = ScriptableObject.CreateInstance<PanelSettings>();
                _ownedPanel.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemeResource); // without a theme the panel draws no text
                _ownedPanel.sortingOrder = _style != null ? _style.SortingOrder : 30000f;
                _ownedPanel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                _ownedPanel.referenceResolution = new Vector2Int(1080, 1920);
                _ownedPanel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                _ownedPanel.match = 0.5f;
                panel = _ownedPanel;
            }
            var document = _host.AddComponent<UIDocument>();
            document.panelSettings = panel;
            GameConsent.AttachInput?.Invoke(document);
            var built = ConsentScreenView.Build(texts, _style, accepted => { Hide(); onChoice(accepted); });
            document.rootVisualElement.Add(built.Root);
        }

        public void Hide()
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            if (_ownedPanel != null) UnityEngine.Object.Destroy(_ownedPanel);
            _ownedPanel = null;
        }
    }
}
