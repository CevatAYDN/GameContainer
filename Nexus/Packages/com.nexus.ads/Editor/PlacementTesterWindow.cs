using System.Collections.Generic;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// Window > Nexus Ads > Placement Tester: every placement, its readiness, a Show button, the result
    /// and the rule that blocked it, plus the live event log, all against the running bootstrap in Play
    /// Mode. Reads events through the telemetry fan-out; it owns no rule.
    /// </summary>
    public sealed class PlacementTesterWindow : EditorWindow, IAdTelemetry
    {
        private const int LogLines = 200;
        private readonly List<string> _log = new List<string>();
        private readonly Dictionary<string, string> _lastResult = new Dictionary<string, string>();
        private AdsBootstrap _bound;
        private Vector2 _scroll, _logScroll;
        private string _lastViolation;

        [MenuItem("Window/Nexus Ads/Placement Tester")]
        public static void Open() => GetWindow<PlacementTesterWindow>("Placement Tester").Show();

        private void OnEnable() => EditorApplication.update += Poll;

        private void OnDisable()
        {
            EditorApplication.update -= Poll;
            Unbind();
        }

        private void Poll()
        {
            var bootstrap = Application.isPlaying ? AdsBootstrap.Instance : null;
            if (bootstrap != _bound) { Unbind(); Bind(bootstrap); }
            Repaint();
        }

        private void Bind(AdsBootstrap bootstrap)
        {
            _bound = bootstrap;
            if (bootstrap == null || bootstrap.Controller == null) return;
            bootstrap.Telemetry.AddSink(this);
            bootstrap.Controller.Violation += OnViolation;
        }

        private void Unbind()
        {
            if (_bound == null) return;
            if (_bound.Telemetry != null) _bound.Telemetry.RemoveSink(this);
            if (_bound.Controller != null) _bound.Controller.Violation -= OnViolation;
            _bound = null;
        }

        private void OnViolation(PolicyViolation v) => _lastViolation = v.ToString();

        public void Record(in AdEvent e)
        {
            _log.Add(System.DateTime.Now.ToString("HH:mm:ss.fff") + "  " + e);
            if (_log.Count > LogLines) _log.RemoveAt(0);
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || _bound == null || _bound.Controller == null)
            {
                EditorGUILayout.HelpBox("Enter Play Mode with an AdsBootstrap in the scene; the editor runs the simulator.", MessageType.Info);
                return;
            }
            var controller = _bound.Controller;
            EditorGUILayout.LabelField("Provider: " + controller.ProviderName + "   state: " + controller.State + "   gate: " + _bound.Gate.Decision + (_bound.Gate.Rule != null ? " (" + _bound.Gate.Rule + ")" : "") + "   kill switch: " + controller.KillSwitch, EditorStyles.wordWrappedLabel);
            var simulator = _bound.Simulator;
            if (simulator != null)
            {
                simulator.Scenario = (SimulatorScenario)EditorGUILayout.EnumPopup("Simulator scenario", simulator.Scenario);
                if (simulator.IsShowing)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Reward")) simulator.CompleteShow(AdResult.Rewarded);
                        if (GUILayout.Button("Close")) simulator.CompleteShow(AdResult.Closed);
                        if (GUILayout.Button("Fail")) simulator.CompleteShow(AdResult.Failed);
                    }
                }
            }
            var receipt = controller.PendingReward;
            if (receipt.HasValue && GUILayout.Button("Acknowledge pending reward " + receipt.Value.Id + " (" + receipt.Value.Placement + ")")) controller.AcknowledgeReward(receipt.Value.Id);

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(220));
            foreach (var placement in _bound.Settings.Placements)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool ready = controller.IsReady(placement.Name);
                    EditorGUILayout.LabelField(placement.Name + " (" + placement.Format + (placement.NaturalBreak ? ", natural break" : "") + ")", GUILayout.Width(260));
                    EditorGUILayout.LabelField(ready ? "ready" : "not ready", GUILayout.Width(70));
                    if (GUILayout.Button("Show", GUILayout.Width(60)))
                    {
                        string name = placement.Name;
                        _lastViolation = null;
                        controller.Show(name, result => _lastResult[name] = result + (result == AdResult.Blocked && _lastViolation != null ? " — " + _lastViolation : ""));
                    }
                    EditorGUILayout.LabelField(_lastResult.TryGetValue(placement.Name, out var last) ? last : "", EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll);
            for (int i = _log.Count - 1; i >= 0; i--) EditorGUILayout.LabelField(_log[i], EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Clear log")) _log.Clear();
        }
    }
}
