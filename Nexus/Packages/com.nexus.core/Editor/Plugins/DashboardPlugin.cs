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

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("NEXUS SYSTEM OVERVIEW");
            _view.Add(toolbar);

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.style.paddingLeft = 15;
            scroll.style.paddingRight = 15;
            scroll.style.paddingTop = 15;
            scroll.style.paddingBottom = 15;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            var cardBg = playing ? NexusEditorStyles.CardBgGreen : NexusEditorStyles.CardBg;
            var titleColor = playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue;

            string statusText = playing
                ? $"<b>PLAY MODE</b> — {contextCount} active context{(contextCount != 1 ? "s" : "")}, {handlerCount} handler{(handlerCount != 1 ? "s" : "")} registered"
                : $"<b>EDITOR MODE</b> — {rootCount} Root{(rootCount != 1 ? "s" : "")} in scene, {handlerCount} handler{(handlerCount != 1 ? "s" : "")} registered";

            var statusIcon = playing ? "\u25B6" : "\u23F8"; // ▶ or ⏸

            var overviewCard = NexusEditorStyles.CreateInfoCard(
                scroll,
                $"{statusIcon}  NEXUS SYSTEM  {(playing ? "● ACTIVE" : "○ STANDBY")}",
                titleColor,
                cardBg,
                statusText);

            if (!playing && rootCount == 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint(
                    "No Roots or Signal Handlers found. Navigate to the <b>Context Wizard</b> tab to generate a root context.");
                hint.style.marginTop = 4;
                overviewCard.Add(hint);
            }

            if (!playing && rootCount > 0 && handlerCount == 0)
            {
                var hint = NexusEditorStyles.CreateHint(
                    "Roots are ready but no Signal Handlers are registered. Check the <b>Signal Explorer</b> to write your first command.");
                hint.style.marginTop = 4;
                overviewCard.Add(hint);
            }

            // Quick Actions Panel
            var actionsCard = NexusEditorStyles.CreateActionGroup(scroll, "QUICK ACTIONS");
            NexusEditorStyles.AddActionButton(actionsCard, "Context Setup Wizard", () => Window.SwitchToPlugin("Wizard"), NexusEditorStyles.BtnBlue);
            NexusEditorStyles.AddActionButton(actionsCard, "Open Hierarchy & Data", () => Window.SwitchToPlugin("Hierarchy"), NexusEditorStyles.BtnTeal);
            NexusEditorStyles.AddActionButton(actionsCard, "View Signal Explorer", () => Window.SwitchToPlugin("Explorer"), NexusEditorStyles.BtnTeal);
            NexusEditorStyles.AddActionButton(actionsCard, "Open Live Tracer", () => Window.SwitchToPlugin("Tracer"), NexusEditorStyles.BtnPurple);

            // Framework Details
            NexusEditorStyles.CreateInfoCard(scroll, "NEXUS OBSERVABLE SUITE", NexusEditorStyles.AccentBlue, NexusEditorStyles.CardBgAlt,
                "Nexus is built on a 0-GC, JIT-free generic observable framework designed for production gaming.\n\n" +
                "• <b>Context Wizard</b>: Handles scaffolding lifecycle components.\n" +
                "• <b>Hierarchy & Data</b>: Live view of DI injection layers and values.\n" +
                "• <b>Signal Explorer</b>: Inspects static signal routes and allows play-mode test firing.\n" +
                "• <b>Live Tracer</b>: Displays time-travel profiling logs for tracing bugs.");

            _view.Add(scroll);
            return _view;
        }
    }
}
