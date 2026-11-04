using System;
using System.Collections.Generic;
using Nexus.Ads.Unity;
using Nexus.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// The Ads section's Placement Tester tab (#193): in Play Mode, against the running <see cref="AdsBootstrap"/>, every
    /// placement with its readiness, a Show button, the result and the rule that blocked it, the simulator's controls and
    /// the live event log. Reads events through the telemetry fan-out; it owns no rule.
    /// </summary>
    internal sealed class PlacementTesterPlugin : NexusEditorPlugin, IAdTelemetry
    {
        private const int LogLines = 200;

        public override string Id => "AdsPlacementTester";
        public override string DisplayName => NexusLang.Get("ads_tab_tester");
        public override int Order => 2;
        public override string Category => "cat_ads";
        public override Color IconColor => NexusEditorStyles.AccentGreen;

        private readonly List<string> _log = new List<string>();
        private readonly Dictionary<string, string> _lastResult = new Dictionary<string, string>();
        private readonly List<Action> _refreshers = new List<Action>();
        private AdsBootstrap _bound;
        private string _lastViolation;
        private VisualElement _body;
        private ScrollView _logView;
        private int _logVersion, _shownLogVersion = -1;

        [MenuItem("Nexus/Ads/Placement Tester", priority = 42)]
        private static void OpenTab() => NexusWindow.Open("AdsPlacementTester");

        public override VisualElement CreateView()
        {
            var view = new VisualElement { style = { flexGrow = 1 } };
            view.Add(NexusEditorStyles.CreateToolbar(NexusLang.Get("ads_tab_tester").ToUpper()));
            _body = new VisualElement { style = { flexGrow = 1 } };
            _body.AddToClassList("nexus-scroll-content");
            view.Add(_body);
            Unbind();
            Bind(Application.isPlaying ? AdsBootstrap.Instance : null);
            return view;
        }

        public override void OnDisable() => Unbind();

        public override void OnUpdate()
        {
            if (_body == null) return;
            var bootstrap = Application.isPlaying ? AdsBootstrap.Instance : null;
            if (bootstrap != _bound) { Unbind(); Bind(bootstrap); }
            foreach (var refresh in _refreshers) refresh();
            if (_logView != null && _shownLogVersion != _logVersion)
            {
                _shownLogVersion = _logVersion;
                _logView.Clear();
                for (int i = _log.Count - 1; i >= 0; i--) _logView.Add(NexusEditorStyles.CreateHint(_log[i]));
            }
        }

        public void Record(in AdEvent e)
        {
            _log.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + e);
            if (_log.Count > LogLines) _log.RemoveAt(0);
            _logVersion++;
        }

        private void OnViolation(PolicyViolation v) => _lastViolation = v.ToString();

        private void Bind(AdsBootstrap bootstrap)
        {
            _bound = bootstrap;
            if (bootstrap != null && bootstrap.Controller != null)
            {
                bootstrap.Telemetry.AddSink(this);
                bootstrap.Controller.Violation += OnViolation;
            }
            Build();
        }

        private void Unbind()
        {
            if (_bound == null) return;
            if (_bound.Telemetry != null) _bound.Telemetry.RemoveSink(this);
            if (_bound.Controller != null) _bound.Controller.Violation -= OnViolation;
            _bound = null;
        }

        private void Build()
        {
            _refreshers.Clear();
            _logView = null;
            _shownLogVersion = -1;
            if (_body == null) return;
            _body.Clear();
            if (_bound == null || _bound.Controller == null)
            {
                _body.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("ads_tester_idle")));
                return;
            }
            var bound = _bound;
            var controller = bound.Controller;

            var state = NexusEditorStyles.CreateBody(string.Empty);
            _refreshers.Add(() => state.text = string.Format(NexusLang.Get("ads_tester_state"), controller.ProviderName, controller.State,
                bound.Gate.Decision + (bound.Gate.Rule != null ? " (" + bound.Gate.Rule + ")" : ""), controller.KillSwitch));
            _body.Add(state);

            var simulator = bound.Simulator;
            if (simulator != null)
            {
                var scenario = new EnumField(NexusLang.Get("ads_tester_scenario"), simulator.Scenario);
                scenario.RegisterValueChangedCallback(e => simulator.Scenario = (SimulatorScenario)e.newValue);
                _body.Add(scenario);
                var showing = AdsViews.Row(_body);
                showing.Add(NexusEditorStyles.CreateButton("Reward", () => simulator.CompleteShow(AdResult.Rewarded), NexusEditorStyles.BtnGreen));
                var close = NexusEditorStyles.CreateButton("Close", () => simulator.CompleteShow(AdResult.Closed), NexusEditorStyles.BtnGray);
                showing.Add(close);
                showing.Add(NexusEditorStyles.CreateButton("Fail", () => simulator.CompleteShow(AdResult.Failed), NexusEditorStyles.BtnRed));
                _refreshers.Add(() =>
                {
                    showing.style.display = simulator.IsShowing ? DisplayStyle.Flex : DisplayStyle.None;
                    close.SetEnabled(simulator.CanClose); // MinShowSeconds holds the close back (issue #132)
                });
            }

            var acknowledge = NexusEditorStyles.CreateButton(string.Empty, () =>
            {
                var receipt = controller.PendingReward;
                if (receipt.HasValue) controller.AcknowledgeReward(receipt.Value.Id);
            }, NexusEditorStyles.BtnBlue);
            acknowledge.style.alignSelf = Align.FlexStart;
            _refreshers.Add(() =>
            {
                var receipt = controller.PendingReward;
                acknowledge.style.display = receipt.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
                if (receipt.HasValue) acknowledge.text = string.Format(NexusLang.Get("ads_tester_acknowledge"), receipt.Value.Id, receipt.Value.Placement);
            });
            _body.Add(acknowledge);

            var placements = new ScrollView { style = { maxHeight = 220, marginTop = 8 } };
            foreach (var placement in bound.Settings.Placements)
            {
                string name = placement.Name;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 2 } };
                row.Add(new Label(name + " (" + placement.Format + (placement.NaturalBreak ? ", " + NexusLang.Get("ads_tester_natural_break") : "") + ")") { style = { width = 260 } });
                var ready = new Label { style = { width = 70 } };
                row.Add(ready);
                row.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_tester_show"), () =>
                {
                    _lastViolation = null;
                    controller.Show(name, result => _lastResult[name] = result + (result == AdResult.Blocked && _lastViolation != null ? " — " + _lastViolation : ""));
                }, NexusEditorStyles.BtnBlue));
                var last = NexusEditorStyles.CreateHint(string.Empty);
                last.style.flexShrink = 1;
                row.Add(last);
                _refreshers.Add(() =>
                {
                    ready.text = NexusLang.Get(controller.IsReady(name) ? "ads_tester_ready" : "ads_tester_not_ready");
                    last.text = _lastResult.TryGetValue(name, out var result) ? result : "";
                });
                placements.Add(row);
            }
            _body.Add(placements);

            var events = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8 } };
            var title = NexusEditorStyles.CreateTitle(NexusLang.Get("ads_tester_events"), NexusEditorStyles.AccentBlue);
            title.style.flexGrow = 1;
            events.Add(title);
            events.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_tester_clear"), () => { _log.Clear(); _logVersion++; }, NexusEditorStyles.BtnGray));
            _body.Add(events);
            _logView = new ScrollView { style = { flexGrow = 1 } };
            _body.Add(_logView);
        }
    }
}
