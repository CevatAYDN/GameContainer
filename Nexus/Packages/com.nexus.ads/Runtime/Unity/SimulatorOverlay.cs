using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// The fake full-screen ad the simulator shows in the editor and on unlisted devices: a dark
    /// layer with the format and placement, a Close button, a Reward button for rewarded ads and a
    /// Fail button for the show-failure path. Immediate-mode UI so the package needs no canvas.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimulatorOverlay : MonoBehaviour
    {
        private SimulatorProvider _simulator;
        private AdFormat _format;
        private string _placement;
        private Texture2D _backdrop;

        public void Bind(SimulatorProvider simulator)
        {
            _simulator = simulator;
            simulator.Interactive = true;
            simulator.Opened += format => { _format = format; _placement = null; };
        }

        public bool Visible => _simulator != null && _simulator.IsShowing;

        private void OnGUI()
        {
            if (!Visible) return;
            if (_backdrop == null)
            {
                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.92f));
                _backdrop.Apply();
            }
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _backdrop);
            float scale = Mathf.Max(1f, Screen.dpi / 160f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = Screen.height / scale;
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, normal = { textColor = Color.white } };
            GUI.Label(new Rect(0, h * 0.3f, w, 40), "Simulated " + _format + " ad", style);
            style.fontSize = 14;
            GUI.Label(new Rect(0, h * 0.3f + 40, w, 30), "scenario: " + _simulator.Scenario, style);
            float bw = Mathf.Min(220f, w * 0.6f), bh = 44f, x = (w - bw) / 2f, y = h * 0.55f;
            if (_format == AdFormat.Rewarded && GUI.Button(new Rect(x, y, bw, bh), "Watch to the end (reward)")) _simulator.CompleteShow(AdResult.Rewarded);
            if (GUI.Button(new Rect(x, y + bh + 12, bw, bh), _format == AdFormat.Rewarded ? "Close early (no reward)" : "Close")) _simulator.CompleteShow(AdResult.Closed);
            if (GUI.Button(new Rect(x, y + 2 * (bh + 12), bw, bh), "Simulate show failure")) _simulator.CompleteShow(AdResult.Failed);
            GUI.matrix = Matrix4x4.identity;
        }

        private void OnDestroy()
        {
            if (_backdrop != null) Destroy(_backdrop);
        }
    }
}
