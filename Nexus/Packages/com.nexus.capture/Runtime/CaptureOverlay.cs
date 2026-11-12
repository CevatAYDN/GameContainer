using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Capture
{
    /// <summary>
    /// Runtime Game-view HUD overlay showing capture status, speed and pause; hidden while recording so it never lands in the video.
    /// </summary>
    internal static class CaptureOverlay
    {
        private static GameObject s_gameObject;
        private static UIDocument s_document;
        private static PanelSettings s_panelSettings;
        private static Label s_speedLabel;
        private static Label s_pausedLabel;
        private static bool s_subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            EnsureSubscribed();
        }

        internal static void EnsureSubscribed()
        {
            if (s_subscribed) return;
            CaptureModeService.OpenChanged += OnOpenChanged;
            s_subscribed = true;
        }

        private static void OnOpenChanged(bool open)
        {
            if (open)
            {
                Create();
            }
            else
            {
                Destroy();
            }
        }

        private static void Create()
        {
            if (s_gameObject != null) return;

            s_gameObject = new GameObject("NexusCaptureOverlay");
            s_gameObject.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(s_gameObject);

            s_panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            s_panelSettings.name = "NexusCapturePanelSettings";
            s_panelSettings.sortingOrder = 32000;
            s_panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;

            s_document = s_gameObject.AddComponent<UIDocument>();
            s_document.panelSettings = s_panelSettings;

            CaptureModeService.Own.Add(s_gameObject);
            CaptureModeService.Own.Add(s_document);
            CaptureModeService.Own.Add(s_panelSettings);

            BuildVisualTree();

            CaptureModeService.StateChanged += UpdateState;
            UpdateState();
        }

        private static void BuildVisualTree()
        {
            if (s_document == null) return;
            var root = s_document.rootVisualElement;
            if (root == null) return;

            root.style.position = Position.Absolute;
            root.style.top = 12;
            root.style.left = 12;
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.12f, 0.85f));
            root.style.paddingTop = 4;
            root.style.paddingBottom = 4;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.borderTopLeftRadius = 4;
            root.style.borderTopRightRadius = 4;
            root.style.borderBottomLeftRadius = 4;
            root.style.borderBottomRightRadius = 4;

            var titleLabel = new Label("CAPTURE");
            titleLabel.style.color = new StyleColor(new Color(0.2f, 0.65f, 1f));
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginRight = 6;
            root.Add(titleLabel);

            s_speedLabel = new Label("1x");
            s_speedLabel.style.color = new StyleColor(Color.white);
            s_speedLabel.style.marginRight = 6;
            root.Add(s_speedLabel);

            s_pausedLabel = new Label("PAUSED");
            s_pausedLabel.style.color = new StyleColor(new Color(1f, 0.65f, 0.25f));
            s_pausedLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            s_pausedLabel.style.marginRight = 6;
            s_pausedLabel.style.display = DisplayStyle.None;
            root.Add(s_pausedLabel);
        }

        private static void UpdateState()
        {
            if (s_document == null || s_document.rootVisualElement == null) return;

            var root = s_document.rootVisualElement;
            if (CaptureModeService.IsRecording)
            {
                root.style.display = DisplayStyle.None;
                return;
            }

            root.style.display = DisplayStyle.Flex;

            if (s_speedLabel != null)
            {
                s_speedLabel.text = $"{CaptureModeService.Speed:0.##}x";
            }

            if (s_pausedLabel != null)
            {
                s_pausedLabel.style.display = CaptureModeService.Paused ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static void Destroy()
        {
            CaptureModeService.StateChanged -= UpdateState;

            if (s_gameObject != null)
            {
                CaptureModeService.Own.Remove(s_gameObject);
            }
            if (s_document != null)
            {
                CaptureModeService.Own.Remove(s_document);
            }
            if (s_panelSettings != null)
            {
                CaptureModeService.Own.Remove(s_panelSettings);
                UnityEngine.Object.Destroy(s_panelSettings);
                s_panelSettings = null;
            }

            if (s_gameObject != null)
            {
                UnityEngine.Object.Destroy(s_gameObject);
                s_gameObject = null;
            }

            s_document = null;
            s_speedLabel = null;
            s_pausedLabel = null;
        }
    }
}
