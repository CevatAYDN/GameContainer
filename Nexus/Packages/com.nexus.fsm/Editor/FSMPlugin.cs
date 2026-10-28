using System;
using System.Collections.Generic;
using UnityEditor;
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
        private volatile bool _rebuildPending;
        private readonly object _historyLock = new();
        private double _lastRebuildTime;

        // Editor-observed transition history keyed by machine instance (weak-ish; cleared on rebind).
        private readonly Dictionary<IGameStateMachine, List<string>> _history = new();
        private readonly Dictionary<IGameStateMachine, string> _lastState = new();

        // Event-driven subscriptions for concrete GameStateMachine instances. The 300 ms
        // schedule stays for machine discovery + card layout, but transition history itself
        // updates in real time via OnStateChanged — nothing is missed, no diffing needed.
        private readonly Dictionary<GameStateMachine, System.Action<StateTransitionRecord>> _subscribed = new();
        private readonly List<(string ctxLabel, IGameStateMachine machine)> _machines = new();
        private readonly HashSet<IGameStateMachine> _live = new();
        private readonly List<IGameStateMachine> _stale = new();
        private readonly List<KeyValuePair<GameStateMachine, Action<StateTransitionRecord>>> _staleSubscriptions = new();
        private readonly Dictionary<IGameStateMachine, VisualElement> _cards = new();
        private readonly Dictionary<IGameStateMachine, int> _configVersions = new();
        private readonly HashSet<IGameStateMachine> _dirty = new();
        private VisualElement _emptyState;
        private bool _emptyStatePlaying;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };
            _view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("fsm_toolbar")));

            _content = new ScrollView { style = { flexGrow = 1, paddingLeft = 10, paddingRight = 10, paddingTop = 8 } };
            _view.Add(_content);

            _statusBar = NexusEditorStyles.CreateStatusBar();
            _view.Add(_statusBar);

            _rebuildPending = true;
            Render();
            return _view;
        }

        public override void OnUpdate()
        {
            if (!_rebuildPending && EditorApplication.timeSinceStartup - _lastRebuildTime < 0.3)
                return;
            _rebuildPending = false;
            _lastRebuildTime = EditorApplication.timeSinceStartup;
            Render();
        }

        public override void OnDisable()
        {
            lock (_historyLock) DisableView();
            base.OnDisable();
        }

        private void DisableView()
        {
            _rebuildPending = false;
            foreach (var kvp in _subscribed)
                kvp.Key.OnStateChanged -= kvp.Value;
            _subscribed.Clear();
            _cards.Clear();
            _history.Clear();
            _lastState.Clear();
            _configVersions.Clear();
            _dirty.Clear();
            _machines.Clear();
            _live.Clear();
            _stale.Clear();
            _staleSubscriptions.Clear();
            _lastRebuildTime = 0;
            _content?.Clear();
            _emptyState = null;
            _emptyStatePlaying = false;
        }

        private void Render()
        {
            lock (_historyLock) RenderView();
        }

        private void RenderView()
        {
            var machines = CollectMachines();
            ObserveTransitions(machines);

            _stale.Clear();
            foreach (var pair in _cards)
                if (!_live.Contains(pair.Key)) _stale.Add(pair.Key);
            foreach (var machine in _stale)
            {
                _cards[machine].RemoveFromHierarchy();
                _cards.Remove(machine);
                _configVersions.Remove(machine);
                _dirty.Remove(machine);
            }

            if (machines.Count == 0)
            {
                if (_emptyState == null || _emptyStatePlaying != Application.isPlaying)
                {
                    _emptyState?.RemoveFromHierarchy();
                    _emptyStatePlaying = Application.isPlaying;
                    _emptyState = NexusEditorStyles.CreateEmptyState(
                        Application.isPlaying
                            ? NexusLang.Get("fsm_empty_playing")
                            : NexusLang.Get("fsm_empty_editmode"));
                    _content.Add(_emptyState);
                }
                if (_statusBar != null) _statusBar.text = string.Format(NexusLang.Get("fsm_status"), 0);
                return;
            }

            if (_emptyState != null) { _emptyState.RemoveFromHierarchy(); _emptyState = null; }
            foreach (var (ctxLabel, machine) in machines)
            {
                int version = machine is GameStateMachine concrete ? concrete.ConfigurationVersion : 0;
                if (_cards.TryGetValue(machine, out var card) && !_dirty.Contains(machine)
                    && _configVersions.TryGetValue(machine, out int previous) && previous == version) continue;
                var replacement = BuildMachineCard(ctxLabel, machine);
                if (card != null)
                {
                    int index = _content.contentContainer.IndexOf(card);
                    card.RemoveFromHierarchy();
                    _content.Insert(index, replacement);
                }
                else _content.Add(replacement);
                _cards[machine] = replacement;
                _configVersions[machine] = version;
                _dirty.Remove(machine);
            }

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
            card.Add(NexusVisualization.CreateStatRow(NexusLang.Get("fsm_current_state"), currentName, currentColor));

            var concrete = machine as GameStateMachine;
            if (concrete != null)
            {
                var errorName = concrete.ErrorStateType?.Name ?? NexusLang.Get("fsm_not_set");
                var errorColor = concrete.ErrorStateType != null ? NexusEditorStyles.AccentOrange : NexusEditorStyles.TextSecondary;
                card.Add(NexusVisualization.CreateStatRow(NexusLang.Get("fsm_error_state"), errorName, errorColor));

                var registered = concrete.RegisteredStateTypes;
                card.Add(NexusVisualization.CreateStatRow(NexusLang.Get("fsm_registered_states"), registered.Count.ToString(), NexusEditorStyles.AccentPurpleText));

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
                for (int i = hist.Count - 1; i >= 0; i--)
                    logBox.Add(new Label(hist[i]) { style = { fontSize = 9, color = NexusEditorStyles.TextSecondary } });
                card.Add(logBox);
            }

            return card;
        }

        private List<(string ctxLabel, IGameStateMachine machine)> CollectMachines()
        {
            var result = _machines;
            result.Clear();
            _live.Clear();
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null) return result;

            foreach (var ctx in contexts)
            {
                IGameStateMachine machine = null;
                try { machine = ctx.TryResolve<IGameStateMachine>(); }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus FSM] Machine resolution failed during collect for context '{ctx?.ScopeTag}': {ex.Message}");
                }

                if (machine == null || !_live.Add(machine)) continue;
                result.Add((ctx.ScopeTag ?? NexusLang.Get("fsm_fallback_context"), machine));
            }
            return result;
        }

        private void ObserveTransitions(List<(string ctxLabel, IGameStateMachine machine)> machines)
        {
            var live = _live;
            foreach (var (_, machine) in machines)
            {
                live.Add(machine);
                if (machine is GameStateMachine concrete)
                {
                    // Event-driven: subscribe once per concrete machine instance.
                    if (!_subscribed.ContainsKey(concrete))
                    {
                        System.Action<StateTransitionRecord> handler = r => OnMachineTransition(concrete, r);
                        _subscribed[concrete] = handler;
                        concrete.OnStateChanged += handler;
                    }
                    var current = concrete.CurrentState?.GetType().Name ?? NexusLang.Get("fsm_no_state");
                    if (!_lastState.TryGetValue(machine, out var previous) || previous != current)
                    {
                        _lastState[machine] = current;
                        _dirty.Add(machine);
                    }
                }
                else
                {
                    // Custom IGameStateMachine implementations expose only CurrentState —
                    // keep the polling diff fallback for them.
                    var current = machine.CurrentState?.GetType().Name ?? NexusLang.Get("fsm_no_state");
                    if (!_lastState.TryGetValue(machine, out var last) || last != current)
                    {
                        _lastState[machine] = current;
                        _dirty.Add(machine);
                        if (last != null) // skip the very first observation
                        {
                            AppendHistory(machine, $"{DateTime.Now:HH:mm:ss}  {last} → {current}");
                        }
                    }
                }
            }

            // Drop bookkeeping/subscriptions for machines that are no longer active or destroyed.
            var staleSubs = _staleSubscriptions;
            staleSubs.Clear();
            foreach (var kvp in _subscribed)
            {
                if (kvp.Key == null || !live.Contains(kvp.Key))
                {
                    if (kvp.Key != null)
                    {
                        try { kvp.Key.OnStateChanged -= kvp.Value; }
                        catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[Nexus FSM] Failed to unsubscribe from state change: {ex.Message}"); }
                    }
                    staleSubs.Add(kvp);
                }
            }
            foreach (var kvp in staleSubs)
            {
                _subscribed.Remove(kvp.Key);
                _history.Remove(kvp.Key);
            }

            var stale = _stale;
            stale.Clear();
            foreach (var machine in _lastState.Keys)
                if (!live.Contains(machine)) stale.Add(machine);
            foreach (var m in stale) { _lastState.Remove(m); _history.Remove(m); }
        }

        private void OnMachineTransition(GameStateMachine machine, StateTransitionRecord record)
        {
            string statusMark = record.Status == StateTransitionStatus.Success
                ? ""
                : $"  [{record.Status}]";
            AppendHistory(machine,
                $"{DateTime.Now:HH:mm:ss}  {record.FromState ?? "—"} → {record.ToState ?? "—"}{statusMark}  ({record.DurationMs:F0} ms)");
        }

        private void AppendHistory(IGameStateMachine machine, string line)
        {
            lock (_historyLock)
            {
                if (machine is GameStateMachine concrete && !_subscribed.ContainsKey(concrete)) return;
                if (!_history.TryGetValue(machine, out var hist))
                    _history[machine] = hist = new List<string>();
                hist.Add(line);
                if (hist.Count > MaxHistory) hist.RemoveAt(0);
                _dirty.Add(machine);
                _rebuildPending = true;
            }
        }
    }
}
