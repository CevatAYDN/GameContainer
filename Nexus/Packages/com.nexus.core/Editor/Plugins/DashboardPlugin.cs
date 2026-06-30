using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class DashboardPlugin : NexusEditorPlugin
    {
        public override string Id => "Dashboard";
        public override string DisplayName => "Dashboard";
        public override int Order => 0;

        private VisualElement _view;
        private Label _contextStat;
        private Label _handlerStat;
        private Label _rootStat;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("NEXUS DASHBOARD");
            _view.Add(toolbar);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.style.paddingLeft = 20;
            scroll.style.paddingRight = 20;
            scroll.style.paddingTop = 20;
            scroll.style.paddingBottom = 20;

            BuildStatusSection(scroll);
            BuildQuickActions(scroll);
            BuildFrameworkInfo(scroll);

            _view.Add(scroll);

            // Periodic refresh
            _view.schedule.Execute(RefreshStats).Every(2000);

            return _view;
        }

        private void BuildStatusSection(VisualElement parent)
        {
            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            var cardBg = playing ? NexusEditorStyles.CardBgGreen : NexusEditorStyles.CardBgBlue;
            var titleColor = playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue;
            var statusSymbol = playing ? "●" : "○";
            var statusText = playing ? "ACTIVE" : "STANDBY";

            // Status header
            var statusCard = NexusEditorStyles.CreateCard(cardBg);
            var statusRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 12 } };

            var statusDot = NexusEditorStyles.CreateStatusDot(titleColor, 12);
            statusRow.Add(statusDot);

            var statusLabel = new Label($"  SYSTEM {statusSymbol} {statusText}")
            {
                style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(titleColor) }
            };
            statusRow.Add(statusLabel);
            statusCard.Add(statusRow);

            // Stat counters
            var statRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };

            _contextStat = CreateStatBox(statRow, contextCount.ToString(), "Active Contexts", NexusEditorStyles.AccentBlue);
            _handlerStat = CreateStatBox(statRow, handlerCount.ToString(), "Handlers", NexusEditorStyles.AccentPurple);
            _rootStat = CreateStatBox(statRow, rootCount.ToString(), "Scene Roots", NexusEditorStyles.AccentYellow);

            statusCard.Add(statRow);

            // Hint text based on state
            if (!playing && rootCount == 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint("No Roots or Signal Handlers found. Start by creating a Root context via the Context Wizard.");
                hint.style.marginTop = 12;
                statusCard.Add(hint);
            }
            else if (!playing && rootCount > 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint("Roots are ready but no Signal Handlers registered. Write your first command and check the Signal Explorer.");
                hint.style.marginTop = 12;
                statusCard.Add(hint);
            }
            else if (playing)
            {
                var hint = NexusEditorStyles.CreateHint("System is live. Use the Live Tracer to monitor signal chains in real-time.");
                hint.style.marginTop = 12;
                statusCard.Add(hint);
            }

            parent.Add(statusCard);
        }

        private Label CreateStatBox(VisualElement parent, string value, string label, Color accentColor)
        {
            var box = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center } };

            var valLabel = new Label(value)
            {
                style = { fontSize = 28, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(accentColor) }
            };
            box.Add(valLabel);

            var descLabel = new Label(label)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 4 }
            };
            box.Add(descLabel);

            parent.Add(box);
            return valLabel;
        }

        private void BuildQuickActions(VisualElement parent)
        {
            var groupCard = NexusEditorStyles.CreateActionGroup(parent, "QUICK ACTIONS");

            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };

            AddActionCard(buttonRow, "Context Wizard", "Create and manage Root contexts", NexusEditorStyles.BtnBlue, () => Window.SwitchToPlugin("Wizard"));
            AddActionCard(buttonRow, "Hierarchy & Data", "Inspect DI container live", NexusEditorStyles.BtnTeal, () => Window.SwitchToPlugin("Hierarchy"));
            AddActionCard(buttonRow, "Signal Explorer", "View signal mappings & test fire", NexusEditorStyles.BtnPurple, () => Window.SwitchToPlugin("Explorer"));
            AddActionCard(buttonRow, "Live Tracer", "Monitor signal chains in real-time", NexusEditorStyles.BtnGray, () => Window.SwitchToPlugin("Tracer"));

            groupCard.Add(buttonRow);
        }

        private void AddActionCard(VisualElement parent, string title, string description, Color btnColor, System.Action onClick)
        {
            var card = new VisualElement();
            card.AddToClassList(NexusEditorStyles.ClassDashboardActionCard);

            var titleLabel = new Label(title)
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue), marginBottom = 4 }
            };
            card.Add(titleLabel);

            var descLabel = new Label(description)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), marginBottom = 8, whiteSpace = WhiteSpace.Normal }
            };
            card.Add(descLabel);

            var btn = NexusEditorStyles.CreateButton("Open", onClick, btnColor);
            btn.style.marginTop = 0;
            btn.style.marginBottom = 0;
            btn.style.alignSelf = Align.FlexStart;
            card.Add(btn);

            // Click the whole card
            card.RegisterCallback<MouseDownEvent>(evt => onClick());

            parent.Add(card);
        }

        private void BuildFrameworkInfo(VisualElement parent)
        {
            var infoCard = NexusEditorStyles.CreateInfoCard(parent, "FRAMEWORK", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBgAlt,
                "Nexus Observable Architecture v0.3.0\n" +
                "Unity 6 • UI Toolkit • MIT License\n\n" +
                "Built on a 0-GC, JIT-free generic observable framework with:\n" +
                "• Causal Tracing — zero-allocation causality tracking\n" +
                "• 4 Execution Modes — Sequential, Concurrent, Exclusive, Composite\n" +
                "• Build Validation — catches priority conflicts before compile\n" +
                "• Auto-Discovery — Lifecycle, Commands, Views and Mediators\n" +
                "• Command Pooling — automatic pooling for 0-GC steady-state\n\n" +
                "Editor Suite: 9 plugins, Code Generator, Live Tracer, Graph Viewer, Type Analyzer");
        }

        private void RefreshStats()
        {
            if (_contextStat == null) return;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _contextStat.text = contextCount.ToString();
            _handlerStat.text = handlerCount.ToString();
            _rootStat.text = rootCount.ToString();
        }
    }
}
