using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// The Status section's tab (#193): the editor's state, one list of every problem the checks
    /// (<see cref="INexusStatusSource"/>) find, each with the action that fixes it, and the installed Nexus packages.
    /// </summary>
    public class StatusPlugin : NexusEditorPlugin
    {
        public override string Id => "Status";
        public override string DisplayName => NexusLang.Get("tab_status");
        public override int Order => 0;
        public override string Category => "cat_status";

        // The last check outlives a view rebuild (a Play Mode switch), not a script reload, after which the code may differ.
        private static List<NexusStatusProblem> s_problems;
        private static DateTime s_checkedAt;

        private Label _stateLabel;
        private Label _stateHint;
        private Label _contextStat;
        private Label _handlerStat;
        private Label _rootStat;
        private Label _summary;
        private VisualElement _problemList;

        public override VisualElement CreateView()
        {
            var view = new VisualElement { style = { flexGrow = 1 } };
            view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("tab_status").ToUpper()));
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.AddToClassList("nexus-scroll-content");
            BuildStateCard(scroll);
            BuildProblemsCard(scroll);
            BuildPackagesCard(scroll);
            view.Add(scroll);
            return view;
        }

        public override void OnUpdate() => RefreshState();

        /// <summary>Runs every check and lists its problems.</summary>
        internal void Check()
        {
            s_problems = NexusStatus.Collect();
            s_checkedAt = DateTime.Now;
            ShowProblems();
        }

        private void BuildStateCard(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgBlue);
            _stateLabel = new Label();
            _stateLabel.AddToClassList("nexus-status-card-title");
            card.Add(_stateLabel);

            var stats = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _contextStat = AddStat(stats, NexusLang.Get("contexts"), NexusEditorStyles.AccentBlue);
            _handlerStat = AddStat(stats, NexusLang.Get("handlers"), NexusEditorStyles.AccentPurple);
            _rootStat = AddStat(stats, NexusLang.Get("roots"), NexusEditorStyles.AccentYellow);
            card.Add(stats);

            var jumps = new VisualElement();
            jumps.AddToClassList("nexus-actions-row");
            jumps.Add(JumpButton(NexusLang.Get("contexts"), NexusLang.Get("status_tip_contexts"), NexusEditorStyles.AccentBlue, () => Window?.SwitchToPlugin("ContextInspector")));
            jumps.Add(JumpButton(NexusLang.Get("handlers"), NexusLang.Get("status_tip_handlers"), NexusEditorStyles.AccentPurple, () => Window?.SwitchToPlugin("Architecture")));
            card.Add(jumps);

            _stateHint = NexusEditorStyles.CreateHint(string.Empty);
            _stateHint.AddToClassList("nexus-status-hint");
            card.Add(_stateHint);
            parent.Add(card);
            RefreshState();
        }

        private void RefreshState()
        {
            if (_stateLabel == null) return;
            bool playing = Application.isPlaying;
            int contexts = NexusEditorDataProvider.GetActiveContextCount();
            int roots = NexusEditorDataProvider.GetSceneRoots()?.Length ?? 0;
            _stateLabel.text = NexusLang.Get(playing ? "system_active" : "system_standby");
            _stateLabel.style.color = new StyleColor(playing ? NexusEditorStyles.AccentGreen : NexusEditorStyles.AccentBlue);
            _contextStat.text = contexts.ToString();
            _handlerStat.text = NexusEditorDataProvider.GetHandlerCount().ToString();
            _rootStat.text = roots.ToString();
            _stateHint.text = playing ? string.Format(NexusLang.Get("live_hint"), contexts)
                : NexusLang.Get(roots == 0 ? "no_roots" : "ready");
        }

        private void BuildProblemsCard(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;
            var top = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var title = NexusEditorStyles.CreateTitle(NexusLang.Get("status_problems"), NexusEditorStyles.AccentOrange);
            title.style.flexGrow = 1;
            top.Add(title);
            var check = NexusEditorStyles.CreateButton(NexusLang.Get("status_check"), Check, NexusEditorStyles.BtnBlue);
            check.name = "status-check";
            check.tooltip = NexusLang.Get("status_check_tip");
            top.Add(check);
            top.Add(NexusEditorStyles.CreateButton(NexusLang.Get("status_copy"), CopyReport, NexusEditorStyles.BtnGray));
            card.Add(top);

            _summary = NexusEditorStyles.CreateHint(string.Empty);
            card.Add(_summary);
            _problemList = new VisualElement { name = "status-problems" };
            card.Add(_problemList);
            parent.Add(card);
            ShowProblems();
        }

        private void ShowProblems()
        {
            if (_problemList == null) return;
            _problemList.Clear();
            if (s_problems == null)
            {
                _summary.text = NexusLang.Get("status_not_checked");
                return;
            }
            int errors = s_problems.Count(p => p.IsError);
            _summary.text = string.Format(NexusLang.Get("status_summary"), errors, s_problems.Count - errors, s_checkedAt);
            if (s_problems.Count == 0) _problemList.Add(NexusEditorStyles.CreateBody(NexusLang.Get("status_none")));
            foreach (var problem in s_problems.OrderByDescending(p => p.IsError)) // stable: errors first, each in its source's order
                _problemList.Add(CreateRow(problem));
        }

        private static VisualElement CreateRow(NexusStatusProblem problem)
        {
            var accent = problem.IsError ? NexusEditorStyles.AccentRed : NexusEditorStyles.AccentYellow;
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginTop = 4 } };
            var dot = NexusEditorStyles.CreateStatusDot(accent, 8);
            dot.style.marginTop = 4;
            dot.style.marginRight = 6;
            row.Add(dot);
            var source = NexusEditorStyles.CreatePill(problem.Source, new Color(accent.r * 0.18f, accent.g * 0.18f, accent.b * 0.18f), accent);
            source.style.marginRight = 6;
            source.style.flexShrink = 0;
            row.Add(source);
            var message = NexusEditorStyles.CreateBody(problem.Message);
            message.style.flexGrow = 1;
            message.style.flexShrink = 1;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);
            if (problem.Fix != null)
            {
                Button fix = null;
                fix = NexusEditorStyles.CreateButton(problem.FixLabel ?? NexusLang.Get("status_fix"), () =>
                {
                    try { problem.Fix(); }
                    catch (Exception ex) { Debug.LogException(ex); return; }
                    // done until the next check says otherwise
                    row.style.opacity = 0.5f;
                    fix.SetEnabled(false);
                }, NexusEditorStyles.BtnBlue);
                fix.style.flexShrink = 0;
                row.Add(fix);
            }
            return row;
        }

        private static void CopyReport()
        {
            var text = new StringBuilder();
            text.AppendLine($"Nexus status: com.nexus.core {NexusLang.PackageVersion}, Unity {Application.unityVersion}");
            if (s_problems == null) text.AppendLine(NexusLang.Get("status_not_checked"));
            else
                foreach (var problem in s_problems.OrderByDescending(p => p.IsError))
                    text.AppendLine($"{(problem.IsError ? "ERROR" : "WARNING")} {problem.Source}: {problem.Message}");
            EditorGUIUtility.systemCopyBuffer = text.ToString();
        }

        private static void BuildPackagesCard(VisualElement parent)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBgAlt);
            card.style.marginTop = 12;
            card.Add(NexusEditorStyles.CreateTitle(NexusLang.Get("status_packages"), NexusEditorStyles.AccentBlue));
            card.Add(NexusEditorStyles.CreateBody(string.Format(NexusLang.Get("status_unity"), Application.unityVersion)));
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                         .Where(p => p.name.StartsWith("com.nexus.")).OrderBy(p => p.name, StringComparer.Ordinal))
            {
                string hash = package.source == PackageSource.Git ? package.git?.hash : null;
                string origin = string.IsNullOrEmpty(hash) ? package.source.ToString() : "git " + hash.Substring(0, Math.Min(7, hash.Length));
                card.Add(NexusEditorStyles.CreateBody($"{package.name} {package.version} · {origin}"));
            }
            parent.Add(card);
        }

        private static Label AddStat(VisualElement parent, string caption, Color accent)
        {
            var box = new VisualElement();
            box.AddToClassList(NexusEditorStyles.ClassStatBox);
            var value = new Label("0");
            value.AddToClassList(NexusEditorStyles.ClassStatValue);
            value.style.color = new StyleColor(accent);
            box.Add(value);
            var label = new Label(caption);
            label.AddToClassList(NexusEditorStyles.ClassStatLabel);
            box.Add(label);
            parent.Add(box);
            return value;
        }

        private static Button JumpButton(string label, string tooltip, Color accent, Action onClick)
        {
            var button = new Button(onClick) { text = label, tooltip = tooltip };
            button.AddToClassList(NexusEditorStyles.ClassMetricBtn);
            button.style.backgroundColor = new StyleColor(new Color(accent.r, accent.g, accent.b, 0.18f));
            return button;
        }
    }
}
