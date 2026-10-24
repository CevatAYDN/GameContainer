using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// The on-device panel of development builds (never compiled into a release build's behaviour:
    /// the bootstrap adds it only when the build is not Release). Opens with three fingers held for a
    /// second, or F9 in the editor. Shows the provider, the gate decision, the build kind, test mode,
    /// the consent state, the device's advertising id (to paste into the Test tab and the mediation
    /// dashboard), readiness per placement, the last violations and events, and a button that opens
    /// the mediation's own test tool. Immediate-mode UI, no canvas.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AdsDebugPanel : MonoBehaviour, IAdTelemetry
    {
        private const int Kept = 12;
        private readonly List<string> _events = new List<string>();
        private readonly List<string> _violations = new List<string>();
        private AdsBootstrap _bootstrap;
        private bool _open;
        private float _touchStart = -1f;
        private Vector2 _scroll;

        public bool IsOpen => _open;

        public void Bind(AdsBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            bootstrap.Telemetry?.AddSink(this);
            if (bootstrap.Controller != null) bootstrap.Controller.Violation += v => Keep(_violations, v.ToString());
        }

        public void Record(in AdEvent e) => Keep(_events, e.ToString());

        private static void Keep(List<string> list, string line)
        {
            list.Add(line);
            if (list.Count > Kept) list.RemoveAt(0);
        }

        /// <summary>Opens or closes the panel; a game's own debug menu can call it when the legacy input manager is off.</summary>
        public void Toggle() => _open = !_open;

        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F9)) _open = !_open;
            if (Input.touchCount >= 3)
            {
                if (_touchStart < 0f) _touchStart = Time.unscaledTime;
                else if (Time.unscaledTime - _touchStart > 1f) { _open = !_open; _touchStart = float.PositiveInfinity; }
            }
            else _touchStart = -1f;
#endif
            // shortcut: with the Input System alone the gesture is not read; call Toggle() from the game's debug menu until the panel reads Touchscreen.current.
        }

        private void OnGUI()
        {
            if (!_open || _bootstrap == null) return;
            float scale = Mathf.Max(1f, Screen.dpi / 160f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = Screen.height / scale;
            GUILayout.BeginArea(new Rect(8, 8, w - 16, h - 16), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            var controller = _bootstrap.Controller;
            GUILayout.Label("Nexus Ads debug — " + _bootstrap.Build + " build, " + _bootstrap.Platform);
            GUILayout.Label("provider: " + (_bootstrap.Mode == HostMode.Provider ? _bootstrap.Provider.Name : _bootstrap.Mode == HostMode.Simulator ? "simulator" : "none (no ads run)") + "   gate: " + _bootstrap.Gate.Decision + (_bootstrap.Gate.Rule != null ? " (" + _bootstrap.Gate.Rule + ": " + _bootstrap.Gate.Reason + ")" : ""));
            var provider = _bootstrap.Settings != null ? _bootstrap.Settings.ToProviderConfig(_bootstrap.Build, _bootstrap.Platform, _bootstrap.Settings.SelectedMediation) : null;
            if (provider != null) GUILayout.Label("test mode: " + provider.TestMode + "   published test data: " + provider.UsesPublishedTestData + "   test devices: " + provider.TestDeviceIds.Count);
            GUILayout.Label("state: " + (controller != null ? controller.State.ToString() : "no controller") + "   kill switch: " + (controller != null && controller.KillSwitch) + "   privacy options required: " + (controller != null && controller.PrivacyOptionsRequired));
            GUILayout.Label("advertising id (add it to the Test tab and the mediation dashboard):");
            GUILayout.TextField(_bootstrap.DeviceAdvertisingId ?? "");
            if (GUILayout.Button("Copy advertising id")) GUIUtility.systemCopyBuffer = _bootstrap.DeviceAdvertisingId ?? "";
            if (controller != null && _bootstrap.Settings != null)
            {
                GUILayout.Label("placements:");
                foreach (var p in _bootstrap.Settings.Placements) GUILayout.Label("  " + p.Name + " (" + p.Format + "): " + (controller.IsReady(p.Name) ? "ready" : "not ready"));
                var receipt = controller.PendingReward;
                if (receipt.HasValue) GUILayout.Label("pending reward: " + receipt.Value.Id + " (" + receipt.Value.Placement + ")");
                if (GUILayout.Button("Open the mediation's test suite")) controller.OpenTestSuite();
            }
            GUILayout.Label("violations:");
            foreach (var v in _violations) GUILayout.Label("  " + v);
            GUILayout.Label("events:");
            for (int i = _events.Count - 1; i >= 0; i--) GUILayout.Label("  " + _events[i]);
            if (GUILayout.Button("Close")) _open = false;
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
