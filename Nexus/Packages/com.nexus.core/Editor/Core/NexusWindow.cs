using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// Host shell for the Nexus Architecture Suite.
    /// Dynamically loads all implementations of <see cref="INexusEditorPlugin"/> and manages tab switching,
    /// branding sidebar, and event-driven status updates.
    /// </summary>
    public partial class NexusWindow : EditorWindow
    {
        // Distinct colors for each plugin's sidebar icon (used as colored circles).
        private static readonly Dictionary<string, Color> PluginIconColors = new()
        {
            { "Dashboard", new Color(0.3f, 0.8f, 1f) },     // AccentBlue
            { "Wizard", new Color(1f, 0.85f, 0.3f) },       // AccentYellow
            { "Hierarchy", new Color(0.4f, 1f, 0.4f) },     // AccentGreen
            { "Explorer", new Color(0.8f, 0.6f, 0.9f) },    // AccentPurple
            { "Tracer", new Color(1f, 0.7f, 0.2f) },        // AccentOrange
            { "Graph", new Color(0.9f, 0.4f, 0.4f) },       // AccentRed
            { "TypeAnalyzer", new Color(0.6f, 0.6f, 0.6f) },// TextSecondary
            { "GameManager", new Color(0.3f, 1f, 0.8f) },   // AccentTeal
            { "Help", new Color(0.6f, 0.6f, 1f) },         // AccentLavender
        };

        private List<INexusEditorPlugin> _plugins = new();
        private INexusEditorPlugin _activePlugin;

        private VisualElement _sidebar;
        private VisualElement _contentArea;
        private Label _statusBar;

        private HierarchyPlugin _hierarchyPlugin; // Keep reference to update trackers in Play Mode

        [MenuItem("Window/Nexus/Dashboard %#n")]
        public static void ShowWindow()
        {
            var window = GetWindow<NexusWindow>("Nexus Dashboard");
            window.minSize = new Vector2(750, 500);
            window.Show();
        }

        private void OnEnable()
        {
            DiscoverPlugins();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;

            NexusRuntime.OnContextRegistered += OnContextEvent;
            NexusRuntime.OnContextUnregistered += OnContextEvent;

            foreach (var plugin in _plugins)
            {
                try { plugin.OnEnable(); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;

            NexusRuntime.OnContextRegistered -= OnContextEvent;
            NexusRuntime.OnContextUnregistered -= OnContextEvent;

            foreach (var plugin in _plugins)
            {
                try { plugin.OnDisable(); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            UpdateStatusBarText();
            RefreshActivePlugin();
        }

        private void OnHierarchyChanged()
        {
            UpdateStatusBarText();
        }

        private void OnContextEvent(IContext context)
        {
            UpdateStatusBarText();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            // Load USS theme
            NexusEditorStyles.LoadTheme(root);

            root.style.flexDirection = FlexDirection.Row;
            root.style.backgroundColor = new StyleColor(NexusEditorStyles.Background);

            // Left Sidebar
            _sidebar = new VisualElement();
            _sidebar.style.width = 200;
            _sidebar.style.borderRightWidth = 1;
            _sidebar.style.borderRightColor = new StyleColor(NexusEditorStyles.BorderColor);
            _sidebar.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.12f));
            _sidebar.style.paddingTop = 20;
            _sidebar.style.paddingLeft = 8;
            _sidebar.style.paddingRight = 8;

            // Brand Header
            var brandLabel = new Label("NEXUS");
            brandLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            brandLabel.style.fontSize = 22;
            brandLabel.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
            brandLabel.style.marginBottom = 2;
            brandLabel.style.alignSelf = Align.Center;
            brandLabel.style.letterSpacing = 3;
            _sidebar.Add(brandLabel);

            var subtitleLabel = new Label("Architecture Suite");
            subtitleLabel.style.fontSize = 9;
            subtitleLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
            subtitleLabel.style.marginBottom = 24;
            subtitleLabel.style.alignSelf = Align.Center;
            subtitleLabel.style.letterSpacing = 1;
            _sidebar.Add(subtitleLabel);

            // Separator
            var sep = new VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = new StyleColor(NexusEditorStyles.BorderColor);
            sep.style.marginBottom = 8;
            sep.style.marginLeft = 4;
            sep.style.marginRight = 4;
            _sidebar.Add(sep);

            // Dynamic Tab Buttons with icons
            foreach (var plugin in _plugins)
            {
                AddTabButton(plugin.DisplayName, plugin.Id);
            }

            // Version at bottom of sidebar
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            _sidebar.Add(spacer);

            // Language selector in sidebar
            var langContainer = new VisualElement();
            langContainer.style.flexDirection = FlexDirection.Row;
            langContainer.style.justifyContent = Justify.Center;
            langContainer.style.marginBottom = 12;
            langContainer.style.paddingTop = 8;
            langContainer.style.borderTopWidth = 1;
            langContainer.style.borderTopColor = new StyleColor(NexusEditorStyles.BorderColor);

            var enBtn = new Button(() => SetLocale("en")) { text = "EN" };
            enBtn.style.fontSize = 9;
            enBtn.style.paddingLeft = 8;
            enBtn.style.paddingRight = 8;
            enBtn.style.paddingTop = 2;
            enBtn.style.paddingBottom = 2;
            enBtn.style.borderTopLeftRadius = 3;
            enBtn.style.borderBottomLeftRadius = 3;
            enBtn.style.borderTopRightRadius = 0;
            enBtn.style.borderBottomRightRadius = 0;
            enBtn.style.borderRightWidth = 0;

            var trBtn = new Button(() => SetLocale("tr")) { text = "TR" };
            trBtn.style.fontSize = 9;
            trBtn.style.paddingLeft = 8;
            trBtn.style.paddingRight = 8;
            trBtn.style.paddingTop = 2;
            trBtn.style.paddingBottom = 2;
            trBtn.style.borderTopRightRadius = 3;
            trBtn.style.borderBottomRightRadius = 3;
            trBtn.style.borderTopLeftRadius = 0;
            trBtn.style.borderBottomLeftRadius = 0;
            trBtn.style.borderLeftWidth = 0;

            var cur = NexusLang.CurrentLocale;
            if (cur == "tr")
            {
                trBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.HighlightBg);
                trBtn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                trBtn.style.unityFontStyleAndWeight = FontStyle.Bold;

                enBtn.style.backgroundColor = new StyleColor(Color.clear);
                enBtn.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
                enBtn.style.unityFontStyleAndWeight = FontStyle.Normal;
            }
            else
            {
                enBtn.style.backgroundColor = new StyleColor(NexusEditorStyles.HighlightBg);
                enBtn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                enBtn.style.unityFontStyleAndWeight = FontStyle.Bold;

                trBtn.style.backgroundColor = new StyleColor(Color.clear);
                trBtn.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
                trBtn.style.unityFontStyleAndWeight = FontStyle.Normal;
            }

            langContainer.Add(enBtn);
            langContainer.Add(trBtn);
            _sidebar.Add(langContainer);

            var versionLabel = new Label("v0.3.0");
            versionLabel.style.fontSize = 9;
            versionLabel.style.color = new StyleColor(NexusEditorStyles.DimText);
            versionLabel.style.alignSelf = Align.Center;
            versionLabel.style.marginBottom = 8;
            versionLabel.style.paddingTop = 8;
            versionLabel.style.borderTopWidth = 1;
            versionLabel.style.borderTopColor = new StyleColor(NexusEditorStyles.BorderColor);
            _sidebar.Add(versionLabel);

            root.Add(_sidebar);

            // Main Content Container (Right Pane)
            var rightPanel = new VisualElement();
            rightPanel.style.flexGrow = 1;
            rightPanel.style.flexDirection = FlexDirection.Column;

            _contentArea = new VisualElement();
            _contentArea.style.flexGrow = 1;
            rightPanel.Add(_contentArea);

            // Bottom Status Bar
            _statusBar = NexusEditorStyles.CreateStatusBar();
            rightPanel.Add(_statusBar);

            root.Add(rightPanel);

            // Select default tab
            if (_plugins.Count > 0)
            {
                SwitchToPlugin(_plugins[0].Id);
            }

            // Keyboard shortcuts: Ctrl+1..9 for tabs
            root.RegisterCallback<KeyDownEvent>(OnKeyDown);

            UpdateStatusBarText();

            // Scheduler to update Hierarchy trackers when in Play Mode and Hierarchy tab is active
            root.schedule.Execute(OnScheduledUpdate).Every(200);
        }

        private void DiscoverPlugins()
        {
            _plugins.Clear();
            _hierarchyPlugin = null;

            var pluginType = typeof(INexusEditorPlugin);
            var foundPlugins = new List<INexusEditorPlugin>();

            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") || name.StartsWith("UnityEngine"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (pluginType.IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
                        {
                            var plugin = (INexusEditorPlugin)Activator.CreateInstance(type);
                            plugin.Initialize(this);
                            foundPlugins.Add(plugin);

                            if (plugin is HierarchyPlugin hp)
                            {
                                _hierarchyPlugin = hp;
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            // Sort plugins by their predefined order
            _plugins = foundPlugins.OrderBy(p => p.Order).ToList();
        }

        private void AddTabButton(string label, string pluginId)
        {
            var btn = new Button(() => SwitchToPlugin(pluginId));
            btn.name = $"Tab_{pluginId}";
            btn.style.backgroundColor = new StyleColor(Color.clear);
            btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
            btn.style.fontSize = 11;
            btn.style.paddingLeft = 12;
            btn.style.paddingRight = 12;
            btn.style.paddingTop = 8;
            btn.style.paddingBottom = 8;
            btn.style.borderTopLeftRadius = 4;
            btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomLeftRadius = 4;
            btn.style.borderBottomRightRadius = 4;
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            btn.style.marginTop = 2;
            btn.style.marginBottom = 2;
            btn.style.unityFontStyleAndWeight = FontStyle.Normal;
            btn.style.alignItems = Align.Center;
            btn.style.flexDirection = FlexDirection.Row;

            // Add colored dot icon
            if (PluginIconColors.TryGetValue(pluginId, out var iconColor))
            {
                var icon = NexusEditorStyles.CreateColorIcon(iconColor);
                btn.Add(icon);
            }

            // Add label
            var txtLabel = new Label(label);
            btn.Add(txtLabel);

            _sidebar.Add(btn);
        }

        public void SwitchToPlugin(string pluginId)
        {
            var targetPlugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
            if (targetPlugin == null || targetPlugin == _activePlugin) return;

            // Dispose the old plugin view before switching
            if (_activePlugin != null)
            {
                try { _activePlugin.OnDisable(); } catch (Exception ex) { Debug.LogException(ex); }
            }

            _activePlugin = targetPlugin;

            // Highlight active sidebar button
            foreach (var plugin in _plugins)
            {
                var btn = _sidebar.Q<Button>($"Tab_{plugin.Id}");
                if (btn != null)
                {
                    if (plugin.Id == pluginId)
                    {
                        btn.style.backgroundColor = new StyleColor(NexusEditorStyles.HighlightBg);
                        btn.style.color = new StyleColor(NexusEditorStyles.AccentBlue);
                        btn.style.unityFontStyleAndWeight = FontStyle.Bold;
                    }
                    else
                    {
                        btn.style.backgroundColor = new StyleColor(Color.clear);
                        btn.style.color = new StyleColor(NexusEditorStyles.TextPrimary);
                        btn.style.unityFontStyleAndWeight = FontStyle.Normal;
                    }
                }
            }

            RefreshActivePlugin();
        }

        private void RefreshActivePlugin()
        {
            if (_contentArea == null || _activePlugin == null) return;
            _contentArea.Clear();

            try
            {
                _contentArea.Add(_activePlugin.CreateView());
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _contentArea.Add(new Label($"Error loading plugin view: {ex.Message}") { style = { color = Color.red } });
            }
        }

        private void OnScheduledUpdate()
        {
            if (_activePlugin != null && _activePlugin.Id == "Hierarchy" && Application.isPlaying)
            {
                _hierarchyPlugin?.UpdateVisibleTrackers();
            }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!evt.ctrlKey) return;

            int index = -1;
            switch (evt.keyCode)
            {
                case KeyCode.Alpha1: index = 0; break;
                case KeyCode.Alpha2: index = 1; break;
                case KeyCode.Alpha3: index = 2; break;
                case KeyCode.Alpha4: index = 3; break;
                case KeyCode.Alpha5: index = 4; break;
                case KeyCode.Alpha6: index = 5; break;
                case KeyCode.Alpha7: index = 6; break;
                case KeyCode.Alpha8: index = 7; break;
                case KeyCode.Alpha9: index = 8; break;
                case KeyCode.F5: RefreshActivePlugin(); _statusBar.text += " ⚡"; return;
            }

            if (index >= 0 && index < _plugins.Count)
            {
                SwitchToPlugin(_plugins[index].Id);
                evt.StopPropagation();
            }
        }

        private void UpdateStatusBarText()
        {
            if (_statusBar == null) return;

            bool playing = Application.isPlaying;
            int contextCount = NexusEditorDataProvider.GetActiveContextCount();
            int handlerCount = NexusEditorDataProvider.GetHandlerCount();
            var roots = NexusEditorDataProvider.GetSceneRoots();
            int rootCount = roots?.Length ?? 0;

            _statusBar.text = playing
                ? $"Nexus ● ACTIVE  |  {contextCount} context(s) active  |  {handlerCount} static handler(s) registered"
                : $"Nexus ○ STANDBY  |  {rootCount} Root(s) in scene  |  Enter Play Mode to activate";
        }

        private void SetLocale(string locale)
        {
            EditorPrefs.SetString("Nexus_Locale", locale);
            NexusLang.LoadLocale(locale);
            RefreshActivePlugin();
        }
    }
}
