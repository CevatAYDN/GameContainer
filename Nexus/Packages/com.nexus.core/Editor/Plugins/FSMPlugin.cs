using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;
using Nexus.Core.FSM;

namespace Nexus.Editor
{
    /// <summary>
    /// Live visibility for <see cref="IGameStateMachine"/> instances resolved from active contexts:
    /// current state, registered states, configured error state, and an editor-observed transition log.
    /// Closes the FSM editor-coverage gap (previously the FSM subsystem had zero editor visibility).
    /// </summary>
    public class FSMPlugin : NexusEditorPlugin
    {
        public override string Id => "FSM";
        public override string DisplayName => NexusLang.Get("action_fsm_title");
        public override int Order => 13;

        private const int MaxHistory = 24;

        private VisualElement _view;
        private ScrollView _content;
        private Label _statusBar;
        private IVisualElementScheduledItem _refreshSchedule;

        // Editor-observed transition history keyed by machine instance (weak-ish; cleared on rebind).
        private readonly Dictionary<IGameStateMachine, List<string>> _history = new();
        private readonly Dictionary<IGameStateMachine, string> _lastState = new();

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };
            _view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("fsm_toolbar")));

            _content = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 8 } };
            _view.Add(_content);

            _statusBar = NexusEditorStyles.CreateStatusBar();
            _view.Add(_statusBar);

            _refreshSchedule = _view.schedule.Execute(Render).Every(300);
            Render();
            return _view;
        }

        public override void OnDisable()
        {
            _refreshSchedule?.Pause();
            base.OnDisable();
        }

        private void Render()
        {
            var machines = CollectMachines();
            ObserveTransitions(machines);

            _content.Clear();

            if (machines.Count == 0)
            {
                _content.Add(NexusEditorStyles.CreateEmptyState(
                    Application.isPlaying
                        ? NexusLang.Get("fsm_empty_playing")
                        : NexusLang.Get("fsm_empty_editmode")));
                if (_statusBar != null) _statusBar.text = string.Format(NexusLang.Get("fsm_status"), 0);
                return;
            }

            foreach (var (ctxLabel, machine) in machines)
                _content.Add(BuildMachineCard(ctxLabel, machine));

            if (_statusBar != null) _statusBar.text = string.Format(NexusLang.Get("fsm_status"), machines.Count);
        }

        private VisualElement BuildMachineCard(string ctxLabel, IGameStateMachine machine)
        {
            var card = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
            card.style.marginBottom = 8;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            header.Add(NexusEditorStyles.CreateTitle(machine.GetType().Name, NexusEditorStyles.AccentBlue, 12));
            header.Add(NexusEditorStyles.CreatePill(ctxLabel, NexusEditorStyles.CardBgBlue, NexusEditorStyles.AccentBlueText));
            if (Application.isPlaying) header.Add(NexusEditorStyles.CreateLiveBadge());
            card.Add(header);

            var currentName = machine.CurrentState?.GetType().Name ?? NexusLang.Get("fsm_none");
            var currentColor = machine.CurrentState != null ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextSecondary;
            card.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("fsm_current_state"), currentName, currentColor));

            var concrete = machine as GameStateMachine;
            if (concrete != null)
            {
                var errorName = concrete.ErrorStateType?.Name ?? NexusLang.Get("fsm_not_set");
                var errorColor = concrete.ErrorStateType != null ? NexusEditorStyles.AccentOrange : NexusEditorStyles.TextSecondary;
                card.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("fsm_error_state"), errorName, errorColor));

                var registered = concrete.RegisteredStateTypes;
                card.Add(NexusEditorStyles.CreateStatRow(NexusLang.Get("fsm_registered_states"), registered.Count.ToString(), NexusEditorStyles.AccentPurpleText));

                var statesWrap = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4, marginBottom = 4 } };
                foreach (var t in registered)
                {
                    bool isCurrent = machine.CurrentState != null && machine.CurrentState.GetType() == t;
                    statesWrap.Add(NexusEditorStyles.CreatePill(
                        t.Name,
                        isCurrent ? NexusEditorStyles.CardBgGreen : NexusEditorStyles.CardBgAlt,
                        isCurrent ? NexusEditorStyles.AccentGreen : NexusEditorStyles.TextPrimary));
                }
                card.Add(statesWrap);
            }
            else
            {
                card.Add(NexusEditorStyles.CreateHint(NexusLang.Get("fsm_custom_impl")));
            }

            // Editor-observed transition history.
            if (_history.TryGetValue(machine, out var hist) && hist.Count > 0)
            {
                card.Add(NexusEditorStyles.CreateSectionTitle(NexusLang.Get("fsm_transition_log")));
                var logBox = new VisualElement { style = { paddingLeft = 4 } };
                foreach (var line in Enumerable.Reverse(hist))
                    logBox.Add(new Label(line) { style = { fontSize = 9, color = NexusEditorStyles.TextSecondary } });
                card.Add(logBox);
            }

            return card;
        }

        private List<(string ctxLabel, IGameStateMachine machine)> CollectMachines()
        {
            var result = new List<(string, IGameStateMachine)>();
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null) return result;

            var seen = new HashSet<IGameStateMachine>();
            foreach (var ctx in contexts)
            {
                IGameStateMachine machine = null;
                try { machine = ctx.TryResolve<IGameStateMachine>(); }
                catch { /* resolution may throw during teardown; ignore */ }

                if (machine == null || !seen.Add(machine)) continue;
                result.Add((ctx.ScopeTag ?? "context", machine));
            }
            return result;
        }

        private void ObserveTransitions(List<(string ctxLabel, IGameStateMachine machine)> machines)
        {
            var live = new HashSet<IGameStateMachine>();
            foreach (var (_, machine) in machines)
            {
                live.Add(machine);
                var current = machine.CurrentState?.GetType().Name ?? "(none)";
                if (!_lastState.TryGetValue(machine, out var last) || last != current)
                {
                    _lastState[machine] = current;
                    if (last != null) // skip the very first observation
                    {
                        if (!_history.TryGetValue(machine, out var hist))
                            _history[machine] = hist = new List<string>();
                        hist.Add($"{DateTime.Now:HH:mm:ss}  {last} → {current}");
                        if (hist.Count > MaxHistory) hist.RemoveAt(0);
                    }
                }
            }

            // Drop bookkeeping for machines that are no longer active.
            var stale = _lastState.Keys.Where(m => !live.Contains(m)).ToList();
            foreach (var m in stale) { _lastState.Remove(m); _history.Remove(m); }
        }
    }
}
