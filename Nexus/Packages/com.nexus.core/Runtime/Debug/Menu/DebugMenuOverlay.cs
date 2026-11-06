using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Draws a <see cref="DebugMenu"/> (#270) on a <see cref="UIDocument"/> of its own panel, above the game's UI, and opens
    /// or closes it when a third finger touches the screen or Backquote is pressed. The document is made at the first open,
    /// when the scene's <c>EventSystem</c> exists, so <see cref="ToolkitPanelInput"/> can give its panel input.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class DebugMenuOverlay : MonoBehaviour
    {
        // Runtime/Debug/Menu/Resources/NexusDebugMenu.tss: Unity's default runtime theme and the menu's styles.
        internal const string ThemeResource = "NexusDebugMenu";
        private const int Fingers = 3;

        private DebugMenu _menu;
        private UIDocument _document;
        private PanelSettings _panel;
        private IDisposable _safeArea;
        private readonly List<(Toggle toggle, DebugMenuEntry entry)> _toggles = new List<(Toggle, DebugMenuEntry)>();
        private int _touches;
        private bool _dirty;

        internal DebugMenu Menu
        {
            get => _menu;
            set
            {
                if (_menu != null) _menu.Changed -= MarkDirty;
                _menu = value;
                if (_menu != null) _menu.Changed += MarkDirty;
            }
        }

        internal bool IsOpen => _document != null && _document.enabled;
        internal VisualElement Root => _document != null ? _document.rootVisualElement : null;
        internal PanelSettings Panel => _panel;

        internal void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        private void OnDestroy()
        {
            Menu = null;
            _safeArea?.Dispose();
            if (_panel != null) Destroy(_panel);
        }

        private void Update()
        {
            if (OpenGesture()) Toggle();
            if (_dirty && IsOpen) Build();
        }

        private void MarkDirty() => _dirty = true;

        private void Open()
        {
            if (_document == null)
            {
                _panel = ScriptableObject.CreateInstance<PanelSettings>();
                _panel.name = "NexusDebugMenu";
                _panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemeResource);
                _panel.scaleMode = PanelScaleMode.ConstantPhysicalSize; // finger-sized on every screen density
                _panel.sortingOrder = 30000;                            // above the game's panels
                _document = gameObject.AddComponent<UIDocument>();
                _document.panelSettings = _panel;
                ToolkitPanelInput.Current?.Attach(_document);
            }
            else
            {
                _document.enabled = true; // a disabled document dropped its tree
            }
            Build();
        }

        private void Close()
        {
            _safeArea?.Dispose();
            _safeArea = null;
            _toggles.Clear();
            _document.enabled = false;
        }

        // A key press or the touch that brings the fingers on the screen to three; the Input System or the legacy
        // manager, as NexusDebugHUD reads its key (#222).
        private bool OpenGesture()
        {
            bool key;
            int touches;
#if ENABLE_LEGACY_INPUT_MANAGER
            key = Input.GetKeyDown(KeyCode.BackQuote);
            touches = Input.touchCount;
#elif UNITY_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            key = keyboard != null && keyboard.backquoteKey.wasPressedThisFrame;
            touches = 0;
            var screen = UnityEngine.InputSystem.Touchscreen.current;
            if (screen != null)
            {
                var all = screen.touches;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].isInProgress) touches++;
            }
#else
            key = false;
            touches = 0;
#endif
            bool fingers = touches >= Fingers && _touches < Fingers;
            _touches = touches;
            return key || fingers;
        }

        private void Build()
        {
            _dirty = false;
            _toggles.Clear();
            _safeArea?.Dispose();
            var root = _document.rootVisualElement;
            root.Clear();

            var menu = new VisualElement();
            menu.AddToClassList("nexus-debug-menu");
            root.Add(menu);
            _safeArea = SafeArea.Pad(menu);

            var header = new VisualElement();
            header.AddToClassList("nexus-debug-menu__header");
            var title = new Label("Nexus debug menu");
            title.AddToClassList("nexus-debug-menu__title");
            header.Add(title);
            header.Add(new Button(Close) { text = "Close" });
            menu.Add(header);

            var list = new ScrollView();
            menu.Add(list);
            var sections = new List<string>();
            foreach (var entry in Menu.Entries)
                if (!sections.Contains(entry.Section)) sections.Add(entry.Section);
            foreach (var section in sections)
            {
                var heading = new Label(section);
                heading.AddToClassList("nexus-debug-menu__section");
                list.Add(heading);
                foreach (var entry in Menu.Entries)
                    if (entry.Section == section) list.Add(Draw(entry));
            }
        }

        private VisualElement Draw(DebugMenuEntry entry)
        {
            if (!entry.IsToggle) return new Button(() => Invoke(entry, entry.Run)) { text = entry.Label };
            var toggle = new Toggle(entry.Label) { value = entry.Get() };
            toggle.RegisterValueChangedCallback(change => Invoke(entry, () => entry.Set(change.newValue)));
            _toggles.Add((toggle, entry));
            return toggle;
        }

        private void Invoke(DebugMenuEntry entry, Action action)
        {
            try
            {
                action();
                // a button may change what a toggle shows; a Get that throws is caught too (#314 R3-15)
                foreach (var (toggle, shown) in _toggles)
                    toggle.SetValueWithoutNotify(shown.Get());
            }
            catch (Exception ex)
            {
                NexusLog.Error("DebugMenu", entry.Label, null, ex); // the menu stays usable
            }
        }
    }
}
