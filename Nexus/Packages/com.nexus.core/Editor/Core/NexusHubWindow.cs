using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    internal class NexusHubWindow : EditorWindow
    {
        private Label _statusBar;
        private VisualElement _overviewContainer;
        private VisualElement _toolsContainer;
        private VisualElement _handlersContainer;

        [MenuItem("Window/Nexus/Hub %#n")]
        internal static void ShowWindow()
        {
            var window = GetWindow<NexusHubWindow>("Nexus Hub");
            window.minSize = new Vector2(500, 500);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
            {
                var windows = Resources.FindObjectsOfTypeAll<NexusHubWindow>();
                foreach (var w in windows)
                    w.Refresh();
            }
        }

        private void CreateGUI()
        {
            rootVisualElement.style.backgroundColor = new StyleColor(NexusEditorStyles.Background);

            var scrollView = new ScrollView();
            scrollView.style.flexGrow = 1;
            rootVisualElement.Add(scrollView);

            var container = new VisualElement();
            container.style.paddingLeft = 15;
            container.style.paddingRight = 15;
            container.style.paddingTop = 15;
            container.style.paddingBottom = 15;
            scrollView.Add(container);

            _overviewContainer = new VisualElement();
            container.Add(_overviewContainer);

            var toolsHeader = NexusEditorStyles.CreateTitle("TOOLS", NexusEditorStyles.TextSecondary, 10);
            toolsHeader.style.marginTop = 12;
            toolsHeader.style.marginBottom = 6;
            container.Add(toolsHeader);

            _toolsContainer = new VisualElement();
            container.Add(_toolsContainer);

            var handlersHeader = NexusEditorStyles.CreateTitle("SIGNAL HANDLERS", NexusEditorStyles.TextSecondary, 10);
            handlersHeader.style.marginTop = 12;
            handlersHeader.style.marginBottom = 6;
            container.Add(handlersHeader);

            _handlersContainer = new VisualElement();
            container.Add(_handlersContainer);

            _statusBar = NexusEditorStyles.CreateStatusBar();
            rootVisualElement.Add(_statusBar);

            Refresh();
        }

        private void Refresh()
        {
            if (_overviewContainer == null) return;
            _overviewContainer.Clear();
            _toolsContainer.Clear();
            _handlersContainer.Clear();

            bool playing = NexusEditorDataProvider.IsPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            BuildOverview(playing, contextCount, handlerCount, rootCount);
            BuildTools();
            BuildHandlers(playing, handlerCount);
            BuildStatusBar(playing, contextCount, rootCount);
        }

        private void BuildOverview(bool playing, int contextCount, int handlerCount, int rootCount)
        {
            var cardBg = playing ? NexusEditorStyles.CardBgGreen : NexusEditorStyles.CardBg;
            var titleColor = playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue;

            string statusText = playing
                ? $"<b>PLAY MODE</b> — {contextCount} active context{(contextCount != 1 ? "s" : "")}, {handlerCount} handler{(handlerCount != 1 ? "s" : "")} registered"
                : $"<b>EDITOR MODE</b> — {rootCount} Root{(rootCount != 1 ? "s" : "")} in scene, {handlerCount} handler{(handlerCount != 1 ? "s" : "")} registered";

            var statusIcon = playing ? "\u25B6" : "\u23F8"; // ▶ or ⏸

            var overviewCard = NexusEditorStyles.CreateInfoCard(
                _overviewContainer,
                $"{statusIcon}  NEXUS SYSTEM  {(playing ? "● ACTIVE" : "○ STANDBY")}",
                titleColor,
                cardBg,
                statusText);

            if (!playing && rootCount == 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint(
                    "No Roots or Signal Handlers found. Create a Root using the Root Wizard below to get started.");
                hint.style.marginTop = 4;
                overviewCard.Add(hint);
            }

            if (!playing && rootCount > 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint(
                    "Roots are ready but no Signal Handlers are registered. Enter Play Mode to activate contexts.");
                hint.style.marginTop = 4;
                overviewCard.Add(hint);
            }

            if (playing)
            {
                var hint = NexusEditorStyles.CreateHint(
                    "Use the Inspector below to trace signals and commands in real-time.");
                hint.style.marginTop = 4;
                overviewCard.Add(hint);
            }
        }

        private void BuildTools()
        {
            var toolsCard = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);

            AddToolEntry(toolsCard, "Nexus Inspector", "Live signal tracing & time-travel debugging",
                NexusEditorStyles.BtnBlue, NexusInspectorWindow.ShowWindow);
            AddToolEntry(toolsCard, "Context Graph", "Context hierarchy & DI singleton visualization",
                NexusEditorStyles.BtnTeal, ContextGraphWindow.ShowWindow);
            AddToolEntry(toolsCard, "Data Inspector", "Model state & NexusData inspection",
                NexusEditorStyles.BtnPurple, NexusDataInspectorWindow.ShowWindow);
            AddToolEntry(toolsCard, "Signal Explorer", "Browse registered signal handlers",
                NexusEditorStyles.BtnTeal, SignalExplorerWindow.ShowWindow);
            AddToolEntry(toolsCard, "Root Wizard", "Create Root GameObjects & View/Mediator pairs",
                NexusEditorStyles.BtnBlue, RootWizard.ShowWindow);

            _toolsContainer.Add(toolsCard);
        }

        private static void AddToolEntry(VisualElement parent, string name, string description, Color btnColor, System.Action openAction)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;

            var btn = NexusEditorStyles.CreateButton("Open", openAction, btnColor);
            btn.style.marginRight = 10;
            btn.style.marginTop = 0;
            btn.style.marginBottom = 0;
            btn.style.flexShrink = 0;
            row.Add(btn);

            var nameLabel = new Label(name);
            nameLabel.style.color = Color.white;
            nameLabel.style.fontSize = 11;
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.width = 110;
            row.Add(nameLabel);

            var descLabel = new Label(description);
            descLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
            descLabel.style.fontSize = 10;
            descLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(descLabel);

            parent.Add(row);
        }

        private void BuildHandlers(bool playing, int handlerCount)
        {
            if (handlerCount == 0)
            {
                var infoCard = NexusEditorStyles.CreateInfoCard(_handlersContainer,
                    "No handlers registered",
                    NexusEditorStyles.AccentOrange,
                    NexusEditorStyles.CardBgYellow,
                    "Add [SignalHandler] attributes to your command classes, then create a Root and enter Play Mode.");
                return;
            }

            var mappings = NexusEditorDataProvider.GetHandlerMappings();
            var card = NexusEditorStyles.CreateInfoCard(_handlersContainer,
                $"{mappings.Count} signal → command mapping{(mappings.Count != 1 ? "s" : "")}",
                NexusEditorStyles.AccentGreen,
                NexusEditorStyles.CardBg,
                playing ? "Active in current Play session. Open Inspector for live traces."
                        : "Discovered at compile time. Enter Play Mode to activate.");

            foreach (var m in mappings)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;
                row.style.paddingLeft = 5;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor);

                var signalLabel = new Label(m.SignalName);
                signalLabel.style.width = new Length(40, LengthUnit.Percent);
                signalLabel.style.color = new StyleColor(NexusEditorStyles.SignalBlue);
                signalLabel.style.fontSize = 11;
                signalLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(signalLabel);

                var arrowLabel = new Label(" \u2192 "); // →
                arrowLabel.style.color = Color.gray;
                arrowLabel.style.fontSize = 11;
                row.Add(arrowLabel);

                var cmdLabel = new Label(m.CommandName);
                cmdLabel.style.color = Color.white;
                cmdLabel.style.fontSize = 11;
                row.Add(cmdLabel);

                var modeLabel = new Label($" [{m.Mode}]");
                modeLabel.style.color = new StyleColor(NexusEditorStyles.AccentPurple);
                modeLabel.style.fontSize = 10;
                modeLabel.style.marginLeft = StyleKeyword.Auto;
                row.Add(modeLabel);

                card.Add(row);
            }
        }

        private void BuildStatusBar(bool playing, int contextCount, int rootCount)
        {
            _statusBar.text = playing
                ? $"Nexus ● ACTIVE  |  {contextCount} context(s)  |  Press Escape to open Hub"
                : $"Nexus ○ STANDBY  |  {rootCount} Root(s) in scene  |  Enter Play Mode to activate";
        }
    }
}
